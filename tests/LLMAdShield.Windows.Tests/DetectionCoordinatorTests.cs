using System.Runtime.CompilerServices;
using System.Threading.Channels;
using LLMAdShield.Core;
using LLMAdShield.Windows.Configuration;
using LLMAdShield.Windows.Services;

namespace LLMAdShield.Windows.Tests;

public sealed class DetectionCoordinatorTests
{
    [Fact]
    public async Task EmergencyUnmute_CannotBeUndoneByInFlightAcquire()
    {
        var capture = new FakeCaptureSource();
        var audio = new FakeAudioMuteLease(blockAcquire: true);
        var overlay = new FakeOverlayPresenter();
        await using var fixture = CreateFixture(capture, audio, overlay);

        await fixture.Coordinator.StartAsync();
        PushThreeFrames(capture);
        await audio.AcquireEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));

        var emergency = fixture.Coordinator.EmergencyUnmuteAsync();
        await Task.Delay(50);
        audio.AllowAcquire.TrySetResult();
        await emergency.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.False(fixture.Coordinator.AutomaticMode);
        Assert.False(audio.State.IsMuted);
        Assert.False(overlay.IsVisible);
        Assert.Equal(0, overlay.ShowCount);
        Assert.True(audio.ReleaseCount > 0);
    }

    [Fact]
    public async Task Watchdog_ExpiresOverlayWhenCaptureStalls()
    {
        var capture = new FakeCaptureSource();
        var audio = new FakeAudioMuteLease();
        var overlay = new FakeOverlayPresenter();
        var stateMachine = new BalancedDetectionStateMachine(new DetectionPolicy
        {
            MuteLeaseDuration = TimeSpan.FromMilliseconds(150),
            MinimumMuteDuration = TimeSpan.FromMilliseconds(150),
            MaximumResultAge = TimeSpan.FromSeconds(3),
        });
        await using var fixture = CreateFixture(
            capture,
            audio,
            overlay,
            stateMachine: stateMachine,
            settings: AppSettings.Default with { CaptureSilenceTimeout = TimeSpan.FromSeconds(5) });

        await fixture.Coordinator.StartAsync();
        PushThreeFrames(capture);
        await audio.AcquireEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await WaitUntilAsync(() => overlay.IsVisible, TimeSpan.FromSeconds(3));

        await WaitUntilAsync(
            () => !audio.State.IsMuted && !overlay.IsVisible,
            TimeSpan.FromSeconds(3));

        Assert.True(audio.ReleaseCount > 0);
        Assert.True(overlay.HideCount > 0);
    }

    [Fact]
    public async Task BudgetAdmission_StopsBeforeRequestThatCouldCrossCap()
    {
        var capture = new FakeCaptureSource();
        var audio = new FakeAudioMuteLease();
        var overlay = new FakeOverlayPresenter();
        var classifier = new FakeClassifier();
        var settings = AppSettings.Default with
        {
            WarningBudgetUsd = 0.004m,
            MaximumBudgetUsd = 0.005m,
        };
        await using var fixture = CreateFixture(
            capture,
            audio,
            overlay,
            classifier,
            settings: settings);
        var budgetStopped = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Coordinator.StatusChanged += (_, status) =>
        {
            if (status.Phase == CoordinatorPhase.BudgetExhausted)
            {
                budgetStopped.TrySetResult();
            }
        };

        await fixture.Coordinator.StartAsync();
        PushThreeFrames(capture);
        await budgetStopped.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(0, classifier.CallCount);
        Assert.False(audio.State.IsMuted);
        Assert.False(overlay.IsVisible);
    }

    private static CoordinatorFixture CreateFixture(
        FakeCaptureSource capture,
        FakeAudioMuteLease audio,
        FakeOverlayPresenter overlay,
        FakeClassifier? classifier = null,
        IDetectionStateMachine? stateMachine = null,
        AppSettings? settings = null)
    {
        var logDirectory = Path.Combine(
            Path.GetTempPath(),
            "LLMAdShield.Tests",
            Guid.NewGuid().ToString("N"));
        var logger = new JsonlTelemetryLogger(logDirectory);
        var selectedClassifier = classifier ?? new FakeClassifier();
        var coordinator = new DetectionCoordinator(
            settings ?? AppSettings.Default,
            capture,
            selectedClassifier,
            selectedClassifier,
            stateMachine ?? new BalancedDetectionStateMachine(),
            audio,
            overlay,
            logger,
            automaticMode: true);
        return new CoordinatorFixture(coordinator, logDirectory);
    }

    private static void PushThreeFrames(FakeCaptureSource capture)
    {
        var startedAt = DateTimeOffset.UtcNow;
        for (var index = 0; index < 3; index++)
        {
            capture.Push(new CapturedFrame(
                startedAt.AddMilliseconds(index * 10),
                2,
                2,
                "image/jpeg",
                new byte[] { 0xFF, 0xD8, (byte)index, 0xFF, 0xD9 }));
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (!condition())
        {
            await Task.Delay(20, cancellation.Token);
        }
    }

    private sealed class CoordinatorFixture(DetectionCoordinator coordinator, string logDirectory)
        : IAsyncDisposable
    {
        public DetectionCoordinator Coordinator { get; } = coordinator;

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Coordinator.DisposeAsync();
            }
            finally
            {
                if (Directory.Exists(logDirectory))
                {
                    Directory.Delete(logDirectory, recursive: true);
                }
            }
        }
    }

    private sealed class FakeCaptureSource : ICaptureSource
    {
        private readonly Channel<CapturedFrame> _frames = Channel.CreateUnbounded<CapturedFrame>();

        public CaptureSourceDescriptor Source { get; } = new("test", "Test capture");

        public void Push(CapturedFrame frame) => _frames.Writer.TryWrite(frame);

        public async IAsyncEnumerable<CapturedFrame> CaptureAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var frame in _frames.Reader.ReadAllAsync(cancellationToken))
            {
                yield return frame;
            }
        }

        public ValueTask DisposeAsync()
        {
            _frames.Writer.TryComplete();
            while (_frames.Reader.TryRead(out var frame))
            {
                frame.Dispose();
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeClassifier : IAdClassifier
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public ValueTask<ClassificationResult> ClassifyAsync(
            FrameWindow frameWindow,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            var now = DateTimeOffset.UtcNow;
            return ValueTask.FromResult(new ClassificationResult
            {
                Outcome = ClassificationOutcome.Success,
                Model = AppSettings.PrimaryModel,
                FrameWindowEndedAt = frameWindow.EndedAt,
                StartedAt = now,
                CompletedAt = now,
                Verdict = new AdVerdict
                {
                    CurrentLabel = ContentLabel.Commercial,
                    Transition = ContentTransition.AdStart,
                    Confidence = ConfidenceLevel.High,
                    Evidence = [EvidenceKind.CallToAction],
                    NeedsMoreContext = false,
                },
                EstimatedCostUsd = 0.001m,
            });
        }
    }

    private sealed class FakeAudioMuteLease(bool blockAcquire = false) : IAudioMuteLease
    {
        private AudioMuteLeaseState _state = new(false, null);

        public TaskCompletionSource AcquireEntered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllowAcquire { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public int ReleaseCount { get; private set; }

        public AudioMuteLeaseState State => Volatile.Read(ref _state);

        public async ValueTask AcquireOrRenewAsync(
            DateTimeOffset expiresAt,
            MuteReason reason,
            CancellationToken cancellationToken = default)
        {
            AcquireEntered.TrySetResult();
            if (blockAcquire)
            {
                await AllowAcquire.Task.WaitAsync(cancellationToken);
            }

            Volatile.Write(ref _state, new AudioMuteLeaseState(true, expiresAt));
        }

        public ValueTask ReleaseAsync(
            AudioReleaseReason reason,
            CancellationToken cancellationToken = default)
        {
            ReleaseCount++;
            Volatile.Write(ref _state, new AudioMuteLeaseState(false, null));
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() =>
            ReleaseAsync(AudioReleaseReason.Shutdown);
    }

    private sealed class FakeOverlayPresenter : IOverlayPresenter
    {
        private int _isVisible;

        public bool IsVisible => Volatile.Read(ref _isVisible) != 0;

        public int ShowCount { get; private set; }

        public int HideCount { get; private set; }

        public ValueTask ShowAsync(
            OverlayPresentation presentation,
            CancellationToken cancellationToken = default)
        {
            ShowCount++;
            Volatile.Write(ref _isVisible, 1);
            return ValueTask.CompletedTask;
        }

        public ValueTask HideAsync(CancellationToken cancellationToken = default)
        {
            HideCount++;
            Volatile.Write(ref _isVisible, 0);
            return ValueTask.CompletedTask;
        }
    }
}
