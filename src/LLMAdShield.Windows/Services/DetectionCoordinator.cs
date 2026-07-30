using LLMAdShield.Core;
using LLMAdShield.Windows.Configuration;

namespace LLMAdShield.Windows.Services;

internal enum CoordinatorPhase
{
    Starting,
    Capturing,
    Classifying,
    ShadowAdvertisement,
    Muted,
    Paused,
    BudgetExhausted,
    SourceUnavailable,
    Faulted,
    Stopped,
}

internal sealed record CoordinatorStatus(
    CoordinatorPhase Phase,
    string Detail,
    ClassificationResult? Classification,
    DetectionSnapshot Detection,
    BudgetSnapshot Budget);

internal sealed class DetectionCoordinator : IAsyncDisposable
{
    private readonly AppSettings _settings;
    private readonly ICaptureSource _captureSource;
    private readonly IAdClassifier _primaryClassifier;
    private readonly IAdClassifier _escalationClassifier;
    private readonly IDetectionStateMachine _stateMachine;
    private readonly IAudioMuteLease _audioMuteLease;
    private readonly IOverlayPresenter _overlay;
    private readonly IOverlayFaultSource? _overlayFaultSource;
    private readonly JsonlTelemetryLogger _logger;
    private readonly RunBudgetMeter _budget;
    private readonly ClassifierCostProfile _classifierCosts;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _actuationGate = new(1, 1);

    private CancellationTokenSource? _runCancellation;
    private Task? _runTask;
    private bool _automaticMode;
    private bool _disposed;
    private int _consecutiveUncertain;
    private long _lastFrameUtcTicks;
    private int _sourceSilenceReported;

    public DetectionCoordinator(
        AppSettings settings,
        ICaptureSource captureSource,
        IAdClassifier primaryClassifier,
        IAdClassifier escalationClassifier,
        IDetectionStateMachine stateMachine,
        IAudioMuteLease audioMuteLease,
        IOverlayPresenter overlay,
        JsonlTelemetryLogger logger,
        bool automaticMode,
        ClassifierCostProfile? classifierCosts = null)
    {
        _settings = settings;
        _captureSource = captureSource;
        _primaryClassifier = primaryClassifier;
        _escalationClassifier = escalationClassifier;
        _stateMachine = stateMachine;
        _audioMuteLease = audioMuteLease;
        _overlay = overlay;
        _overlayFaultSource = overlay as IOverlayFaultSource;
        if (_overlayFaultSource is not null)
        {
            _overlayFaultSource.Faulted += OverlayFaultSource_Faulted;
        }
        _logger = logger;
        _automaticMode = automaticMode;
        _classifierCosts = classifierCosts ?? new ClassifierCostProfile(
            MaximumRequestCost(settings, settings.OpenAiPrimaryModel),
            MaximumRequestCost(settings, settings.OpenAiEscalationModel));
        _budget = new RunBudgetMeter(new BudgetPolicy
        {
            WarningThresholdUsd = settings.WarningBudgetUsd,
            HardCapUsd = settings.MaximumBudgetUsd,
        });
    }

    public event EventHandler<CoordinatorStatus>? StatusChanged;

    public bool IsRunning => _runTask is { IsCompleted: false };

    public bool AutomaticMode => Volatile.Read(ref _automaticMode);

    public string TelemetryPath => _logger.Path;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsRunning)
            {
                return;
            }

            _runCancellation = new CancellationTokenSource();
            _runTask = RunAsync(_runCancellation.Token);
            Publish(CoordinatorPhase.Starting, "Opening the selected capture source.");
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task SetAutomaticModeAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        Volatile.Write(ref _automaticMode, enabled);
        if (!enabled)
        {
            await FailOpenAsync(
                DetectionReason.ManualStop,
                AudioReleaseReason.ManualStop,
                cancellationToken).ConfigureAwait(false);
            Publish(CoordinatorPhase.Capturing, "Shadow mode: classifications are logged, but audio is untouched.");
        }
    }

    public async Task EmergencyUnmuteAsync(CancellationToken cancellationToken = default)
    {
        Volatile.Write(ref _automaticMode, false);
        await FailOpenAsync(
            DetectionReason.EmergencyUnmute,
            AudioReleaseReason.EmergencyUnmute,
            cancellationToken).ConfigureAwait(false);
        Publish(CoordinatorPhase.Paused, "Emergency unmute activated. Automation is paused; shadow mode remains available.");
    }

    public ValueTask AnnotateAsync(
        ManualAnnotationLabel label,
        CancellationToken cancellationToken = default) =>
        _logger.WriteAsync(new ManualAnnotationMetadataLogRecord
        {
            Timestamp = DateTimeOffset.UtcNow,
            Label = label,
        }, cancellationToken);

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var errors = new List<Exception>();
            var cancellation = _runCancellation;
            var task = _runTask;
            if (cancellation is null || task is null)
            {
                try
                {
                    await FailOpenAsync(
                        DetectionReason.ManualStop,
                        AudioReleaseReason.ManualStop,
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    errors.Add(exception);
                }

                if (errors.Count > 0)
                {
                    throw new AggregateException("LLM AdShield could not complete every fail-open action.", errors);
                }

                return;
            }

            cancellation.Cancel();
            try
            {
                await FailOpenAsync(
                    DetectionReason.ManualStop,
                    AudioReleaseReason.ManualStop,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                errors.Add(exception);
            }

            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                errors.Add(exception);
            }

            cancellation.Dispose();
            _runCancellation = null;
            _runTask = null;
            try
            {
                await FailOpenAsync(
                    DetectionReason.ManualStop,
                    AudioReleaseReason.ManualStop,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                errors.Add(exception);
            }

            Publish(CoordinatorPhase.Stopped, "Detection stopped and the original audio state was restored.");
            if (errors.Count > 0)
            {
                throw new AggregateException("LLM AdShield stopped, but one or more cleanup actions failed.", errors);
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var ringBuffer = new FrameRingBuffer(_settings.FrameRetention, maximumFrameCount: 32);
        using var watchdogCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var watchdogTask = RunWatchdogAsync(watchdogCancellation.Token);
        var nextClassificationAt = DateTimeOffset.MinValue;
        var consecutiveBlackFrames = 0;
        var terminalStatusPublished = false;
        Interlocked.Exchange(ref _lastFrameUtcTicks, DateTimeOffset.UtcNow.UtcDateTime.Ticks);

        try
        {
            Publish(CoordinatorPhase.Capturing, "Capture active. Waiting for three in-memory frames.");
            await foreach (var frame in _captureSource.CaptureAsync(cancellationToken)
                               .WithCancellation(cancellationToken)
                               .ConfigureAwait(false))
            {
                ringBuffer.Add(frame);
                Interlocked.Exchange(ref _lastFrameUtcTicks, frame.CapturedAt.UtcDateTime.Ticks);
                if (Interlocked.Exchange(ref _sourceSilenceReported, 0) != 0)
                {
                    Publish(CoordinatorPhase.Capturing, "Capture resumed. Waiting for a fresh three-frame window.");
                }

                consecutiveBlackFrames = frame.LikelyBlack ? consecutiveBlackFrames + 1 : 0;

                if (consecutiveBlackFrames >= 6)
                {
                    await FailOpenAsync(
                        DetectionReason.SourceUnavailable,
                        AudioReleaseReason.SourceUnavailable,
                        cancellationToken).ConfigureAwait(false);
                    Publish(CoordinatorPhase.SourceUnavailable, "The selected window is blank or protected. No frames are being uploaded.");
                    terminalStatusPublished = true;
                    break;
                }

                var now = DateTimeOffset.UtcNow;
                if (now < nextClassificationAt || !ringBuffer.TryCreateLatestWindow(3, out var window))
                {
                    continue;
                }

                using (window)
                {
                    var primaryMaximumCost = _classifierCosts.PrimaryMaximumRequestCostUsd;
                    if (!_budget.CanLaunchRequestCostingAtMost(primaryMaximumCost))
                    {
                        await HandleBudgetExhaustionAsync(cancellationToken).ConfigureAwait(false);
                        terminalStatusPublished = true;
                        break;
                    }

                    var activeAtLaunch = _stateMachine.Snapshot.IsMuted ||
                        _stateMachine.Snapshot.IsAdvertisementSuspected;
                    var launchedAt = DateTimeOffset.UtcNow;
                    nextClassificationAt = launchedAt +
                        (activeAtLaunch
                            ? _settings.ActiveClassificationInterval
                            : _settings.NormalClassificationInterval);

                    Publish(CoordinatorPhase.Classifying, "Classifying the latest three-frame window.");
                    var result = await ClassifyWithinFreshnessAsync(
                        _primaryClassifier,
                        window,
                        cancellationToken).ConfigureAwait(false);
                    if (await RecordClassificationAsync(result, cancellationToken).ConfigureAwait(false))
                    {
                        await HandleBudgetExhaustionAsync(cancellationToken).ConfigureAwait(false);
                        terminalStatusPublished = true;
                        break;
                    }

                    result = await MaybeEscalateAsync(result, window, cancellationToken).ConfigureAwait(false);
                    if (_budget.Snapshot.Level == BudgetLevel.Exhausted)
                    {
                        await HandleBudgetExhaustionAsync(cancellationToken).ConfigureAwait(false);
                        terminalStatusPublished = true;
                        break;
                    }

                    var decision = _stateMachine.Process(result, DateTimeOffset.UtcNow);
                    await LogDecisionAsync(decision, cancellationToken).ConfigureAwait(false);
                    await ApplyDecisionAsync(decision, cancellationToken).ConfigureAwait(false);
                    PublishFromResult(result, decision);
                }
            }

            if (!cancellationToken.IsCancellationRequested && !terminalStatusPublished)
            {
                await FailOpenAsync(
                    DetectionReason.SourceUnavailable,
                    AudioReleaseReason.SourceUnavailable,
                    CancellationToken.None).ConfigureAwait(false);
                Publish(CoordinatorPhase.SourceUnavailable, "The selected player window closed or capture ended.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await _logger.WriteAsync(new ErrorMetadataLogRecord
            {
                Timestamp = DateTimeOffset.UtcNow,
                Component = "coordinator",
                ErrorCode = exception.GetType().Name,
            }, CancellationToken.None).ConfigureAwait(false);
            await FailOpenAsync(DetectionReason.SourceUnavailable, AudioReleaseReason.Failure, CancellationToken.None)
                .ConfigureAwait(false);
            Publish(CoordinatorPhase.Faulted, $"Detection stopped safely: {exception.GetType().Name}.");
            return;
        }
        finally
        {
            watchdogCancellation.Cancel();
            try
            {
                await watchdogTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                await _logger.WriteAsync(new ErrorMetadataLogRecord
                {
                    Timestamp = DateTimeOffset.UtcNow,
                    Component = "watchdog",
                    ErrorCode = exception.GetType().Name,
                }, CancellationToken.None).ConfigureAwait(false);
            }

            try
            {
                await FailOpenAsync(DetectionReason.Shutdown, AudioReleaseReason.Shutdown, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                await _logger.WriteAsync(new ErrorMetadataLogRecord
                {
                    Timestamp = DateTimeOffset.UtcNow,
                    Component = "fail_open",
                    ErrorCode = exception.GetType().Name,
                }, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private async Task RunWatchdogAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            var now = DateTimeOffset.UtcNow;
            var tickDecision = _stateMachine.Tick(now);
            if (tickDecision.Action != DetectionAction.None)
            {
                await LogDecisionAsync(tickDecision, CancellationToken.None).ConfigureAwait(false);
                await ApplyDecisionAsync(tickDecision, CancellationToken.None).ConfigureAwait(false);
            }

            var lastFrameTicks = Interlocked.Read(ref _lastFrameUtcTicks);
            if (lastFrameTicks == 0 ||
                now - new DateTimeOffset(lastFrameTicks, TimeSpan.Zero) <= _settings.CaptureSilenceTimeout ||
                Interlocked.Exchange(ref _sourceSilenceReported, 1) != 0)
            {
                continue;
            }

            await FailOpenAsync(
                DetectionReason.SourceUnavailable,
                AudioReleaseReason.SourceUnavailable,
                CancellationToken.None).ConfigureAwait(false);
            Publish(
                CoordinatorPhase.Capturing,
                "No fresh frames are arriving. Audio is restored and the overlay is hidden while capture waits to resume.");
        }
    }

    private async Task<ClassificationResult> MaybeEscalateAsync(
        ClassificationResult primary,
        FrameWindow window,
        CancellationToken cancellationToken)
    {
        var uncertain = primary.HasUsableVerdict &&
            (primary.Verdict!.CurrentLabel == ContentLabel.Unknown || primary.Verdict.NeedsMoreContext);
        _consecutiveUncertain = uncertain ? _consecutiveUncertain + 1 : 0;

        if (_consecutiveUncertain < 2 ||
            DateTimeOffset.UtcNow - window.EndedAt >= _settings.MaximumResultAge ||
            !_budget.CanLaunchRequestCostingAtMost(_classifierCosts.EscalationMaximumRequestCostUsd))
        {
            return primary;
        }

        _consecutiveUncertain = 0;
        var escalation = await ClassifyWithinFreshnessAsync(
            _escalationClassifier,
            window,
            cancellationToken).ConfigureAwait(false);
        await RecordClassificationAsync(escalation, cancellationToken).ConfigureAwait(false);
        return escalation.HasUsableVerdict ? escalation : primary;
    }

    private async ValueTask<ClassificationResult> ClassifyWithinFreshnessAsync(
        IAdClassifier classifier,
        FrameWindow window,
        CancellationToken cancellationToken)
    {
        var remaining = _settings.MaximumResultAge - (DateTimeOffset.UtcNow - window.EndedAt);
        if (remaining <= TimeSpan.Zero)
        {
            var now = DateTimeOffset.UtcNow;
            return new ClassificationResult
            {
                Outcome = ClassificationOutcome.Cancelled,
                Model = "not_dispatched",
                FrameWindowEndedAt = window.EndedAt,
                StartedAt = now,
                CompletedAt = now,
                ErrorCode = "stale_before_dispatch",
            };
        }

        using var freshnessCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        freshnessCancellation.CancelAfter(remaining);
        return await classifier.ClassifyAsync(window, freshnessCancellation.Token).ConfigureAwait(false);
    }

    private async Task<bool> RecordClassificationAsync(
        ClassificationResult result,
        CancellationToken cancellationToken)
    {
        var budgetUpdate = _budget.RecordCost(result.EstimatedCostUsd);
        var stale = DateTimeOffset.UtcNow - result.FrameWindowEndedAt > _settings.MaximumResultAge;
        await _logger.WriteAsync(new ClassificationMetadataLogRecord
        {
            Timestamp = DateTimeOffset.UtcNow,
            FrameWindowEndedAt = result.FrameWindowEndedAt,
            Model = result.Model,
            Outcome = result.Outcome,
            CurrentLabel = result.Verdict?.CurrentLabel,
            Transition = result.Verdict?.Transition,
            Confidence = result.Verdict?.Confidence,
            Evidence = result.Verdict?.Evidence ?? [],
            NeedsMoreContext = result.Verdict?.NeedsMoreContext,
            LatencyMilliseconds = result.Latency.TotalMilliseconds,
            Usage = result.Usage,
            EstimatedCostUsd = result.EstimatedCostUsd,
            WasStale = stale,
            ErrorCode = result.ErrorCode,
        }, cancellationToken).ConfigureAwait(false);

        if (budgetUpdate.WarningThresholdCrossed || budgetUpdate.HardCapCrossed)
        {
            await _logger.WriteAsync(new BudgetMetadataLogRecord
            {
                Timestamp = DateTimeOffset.UtcNow,
                SpentUsd = budgetUpdate.Snapshot.SpentUsd,
                Level = budgetUpdate.Snapshot.Level,
                CanLaunchRequest = budgetUpdate.Snapshot.CanLaunchRequest,
            }, cancellationToken).ConfigureAwait(false);

            if (budgetUpdate.WarningThresholdCrossed)
            {
                Publish(
                    _stateMachine.Snapshot.IsMuted ? CoordinatorPhase.Muted : CoordinatorPhase.Capturing,
                    $"Budget warning: estimated API spend reached {budgetUpdate.Snapshot.SpentUsd:C2}. Requests stop at {budgetUpdate.Snapshot.HardCapUsd:C2}.",
                    result);
            }
        }

        return budgetUpdate.Snapshot.Level == BudgetLevel.Exhausted;
    }

    private async Task LogDecisionAsync(DetectionDecision decision, CancellationToken cancellationToken) =>
        await _logger.WriteAsync(new DetectionDecisionMetadataLogRecord
        {
            Timestamp = decision.DecidedAt,
            Action = decision.Action,
            Reason = decision.Reason,
            IsMuted = decision.Snapshot.IsMuted,
            MuteLeaseExpiresAt = decision.Snapshot.MuteLeaseExpiresAt,
        }, cancellationToken).ConfigureAwait(false);

    private async Task ApplyDecisionAsync(DetectionDecision decision, CancellationToken cancellationToken)
    {
        await _actuationGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (decision.Action == DetectionAction.AcquireOrRenewMute)
            {
                if (!AutomaticMode || !_stateMachine.Snapshot.IsMuted)
                {
                    return;
                }

                var expiresAt = decision.Snapshot.MuteLeaseExpiresAt ?? DateTimeOffset.UtcNow.AddSeconds(6);
                try
                {
                    await _audioMuteLease.AcquireOrRenewAsync(
                        expiresAt,
                        MuteReason.AdvertisementDetected,
                        cancellationToken).ConfigureAwait(false);

                    if (!AutomaticMode || !_stateMachine.Snapshot.IsMuted)
                    {
                        await ReleaseOutputsUnderLockAsync(
                            AudioReleaseReason.EmergencyUnmute,
                            CancellationToken.None).ConfigureAwait(false);
                        return;
                    }

                    await _overlay.ShowAsync(OverlayPresentation.AdvertisementMuted, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch
                {
                    await ReleaseOutputsUnderLockAsync(
                        AudioReleaseReason.Failure,
                        CancellationToken.None).ConfigureAwait(false);
                    throw;
                }

                return;
            }

            if (decision.Action == DetectionAction.ReleaseMute)
            {
                await ReleaseOutputsUnderLockAsync(
                    MapReleaseReason(decision.Reason),
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            _actuationGate.Release();
        }
    }

    private async Task HandleBudgetExhaustionAsync(CancellationToken cancellationToken)
    {
        var snapshot = _budget.StopLaunchingRequests();
        await _logger.WriteAsync(new BudgetMetadataLogRecord
        {
            Timestamp = DateTimeOffset.UtcNow,
            SpentUsd = snapshot.SpentUsd,
            Level = snapshot.Level,
            CanLaunchRequest = false,
        }, CancellationToken.None).ConfigureAwait(false);
        await FailOpenAsync(DetectionReason.BudgetCap, AudioReleaseReason.BudgetCap, cancellationToken)
            .ConfigureAwait(false);
        Publish(
            CoordinatorPhase.BudgetExhausted,
            $"The {snapshot.HardCapUsd:C2} run ceiling has been reached. Cloud classification is paused and audio is restored.");
    }

    private async Task FailOpenAsync(
        DetectionReason reason,
        AudioReleaseReason audioReason,
        CancellationToken cancellationToken)
    {
        _stateMachine.ForceFailOpen(DateTimeOffset.UtcNow, reason);
        await _actuationGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await ReleaseOutputsUnderLockAsync(audioReason, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _actuationGate.Release();
        }
    }

    private async Task ReleaseOutputsUnderLockAsync(
        AudioReleaseReason audioReason,
        CancellationToken cancellationToken)
    {
        Exception? audioError = null;
        Exception? overlayError = null;

        try
        {
            await _audioMuteLease.ReleaseAsync(audioReason, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            audioError = exception;
        }

        try
        {
            await _overlay.HideAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            overlayError = exception;
        }

        if (audioError is not null && overlayError is not null)
        {
            throw new AggregateException("Audio restoration and overlay removal both failed.", audioError, overlayError);
        }

        if (audioError is not null)
        {
            throw new InvalidOperationException("The selected audio session could not be restored.", audioError);
        }

        if (overlayError is not null)
        {
            throw new InvalidOperationException("The advertisement overlay could not be removed.", overlayError);
        }
    }

    private static decimal MaximumRequestCost(AppSettings settings, string model)
    {
        var pricing = settings.Pricing[model];
        return pricing.MaximumRequestCostUsd(
            settings.MaximumEstimatedInputTokensPerRequest,
            settings.MaximumOutputTokensPerRequest);
    }

    private void OverlayFaultSource_Faulted(object? sender, OverlayFaultEventArgs eventArgs) =>
        _ = HandleOverlayFaultAsync(eventArgs);

    private async Task HandleOverlayFaultAsync(OverlayFaultEventArgs eventArgs)
    {
        try
        {
            await _logger.WriteAsync(new ErrorMetadataLogRecord
            {
                Timestamp = DateTimeOffset.UtcNow,
                Component = "overlay",
                ErrorCode = eventArgs.ErrorCode,
            }, CancellationToken.None).ConfigureAwait(false);
            await FailOpenAsync(
                DetectionReason.SourceUnavailable,
                AudioReleaseReason.Failure,
                CancellationToken.None).ConfigureAwait(false);
            Publish(CoordinatorPhase.Faulted, "The overlay lost the selected window. Audio was restored and automation stopped safely.");
        }
        catch
        {
            Publish(CoordinatorPhase.Faulted, "The overlay failed. LLM AdShield attempted an emergency audio restore; check the selected session manually.");
        }
    }

    private void PublishFromResult(ClassificationResult result, DetectionDecision decision)
    {
        var phase = decision.Snapshot.IsMuted
            ? AutomaticMode ? CoordinatorPhase.Muted : CoordinatorPhase.ShadowAdvertisement
            : CoordinatorPhase.Capturing;
        var detail = result.HasUsableVerdict
            ? $"{result.Verdict!.CurrentLabel} ({result.Verdict.Confidence}), {result.Latency.TotalMilliseconds:F0} ms"
            : $"Classifier {result.Outcome}: {result.ErrorCode ?? "no verdict"}";
        Publish(phase, detail, result);
    }

    private void Publish(CoordinatorPhase phase, string detail, ClassificationResult? result = null) =>
        StatusChanged?.Invoke(this, new CoordinatorStatus(
            phase,
            detail,
            result,
            _stateMachine.Snapshot,
            _budget.Snapshot));

    private static AudioReleaseReason MapReleaseReason(DetectionReason reason) => reason switch
    {
        DetectionReason.ProgramResumed => AudioReleaseReason.ProgramResumed,
        DetectionReason.PublicSafety => AudioReleaseReason.PublicSafety,
        DetectionReason.LeaseExpired => AudioReleaseReason.LeaseExpired,
        DetectionReason.BudgetCap => AudioReleaseReason.BudgetCap,
        DetectionReason.EmergencyUnmute => AudioReleaseReason.EmergencyUnmute,
        DetectionReason.SourceUnavailable => AudioReleaseReason.SourceUnavailable,
        DetectionReason.Shutdown => AudioReleaseReason.Shutdown,
        _ => AudioReleaseReason.Failure,
    };

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var errors = new List<Exception>();

        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            errors.Add(exception);
        }

        if (_overlayFaultSource is not null)
        {
            _overlayFaultSource.Faulted -= OverlayFaultSource_Faulted;
        }

        try
        {
            await _captureSource.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            errors.Add(exception);
        }

        try
        {
            await _audioMuteLease.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            errors.Add(exception);
        }

        try
        {
            if (_overlay is IAsyncDisposable asyncOverlay)
            {
                await asyncOverlay.DisposeAsync().ConfigureAwait(false);
            }
            else if (_overlay is IDisposable overlay)
            {
                overlay.Dispose();
            }
        }
        catch (Exception exception)
        {
            errors.Add(exception);
        }

        try
        {
            (_primaryClassifier as IDisposable)?.Dispose();
        }
        catch (Exception exception)
        {
            errors.Add(exception);
        }

        try
        {
            (_escalationClassifier as IDisposable)?.Dispose();
        }
        catch (Exception exception)
        {
            errors.Add(exception);
        }

        try
        {
            await _logger.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            errors.Add(exception);
        }

        _actuationGate.Dispose();
        _lifecycleGate.Dispose();

        if (errors.Count > 0)
        {
            throw new AggregateException("One or more LLM AdShield components could not be disposed cleanly.", errors);
        }
    }
}
