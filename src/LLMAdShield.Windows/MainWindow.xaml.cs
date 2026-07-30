using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using LLMAdShield.Core;
using LLMAdShield.Windows.Configuration;
using LLMAdShield.Windows.Platform.Audio;
using LLMAdShield.Windows.Platform.Capture;
using LLMAdShield.Windows.Platform.Hotkeys;
using LLMAdShield.Windows.Platform.Overlay;
using LLMAdShield.Windows.Platform.Security;
using LLMAdShield.Windows.Platform.Tray;
using LLMAdShield.Windows.Services;

namespace LLMAdShield.Windows;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings = AppSettings.Default;
    private readonly WindowsAudioSessionEnumerator _audioEnumerator = new();
    private readonly IApiCredentialStore _credentialStore;
    private readonly ApiCredentialResolver _credentialResolver;
    private readonly ProviderPreferenceStore _providerPreferences = new();
    private readonly object _shutdownSync = new();
    private DetectionCoordinator? _coordinator;
    private GlobalHotkeyManager? _hotkeys;
    private TrayIconController? _trayIcon;
    private Task? _shutdownTask;
    private bool _shutdownStarted;
    private bool _suppressModeEvent;
    private int _terminalCleanupStarted;

    public MainWindow()
    {
        _credentialStore = new WindowsDpapiCredentialStore();
        _credentialResolver = new ApiCredentialResolver(_credentialStore);
        InitializeComponent();
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        WindowTargetCombo.SelectionChanged += WindowTargetCombo_SelectionChanged;
        ShadowModeRadio.Checked += DetectionMode_Checked;
        AutomaticModeRadio.Checked += DetectionMode_Checked;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e) =>
        await RunUiActionAsync(InitializeAsync, "LLM AdShield initialization failed", showDialog: true);

    private async Task InitializeAsync()
    {
        var handle = new WindowInteropHelper(this).EnsureHandle();
        _hotkeys = new GlobalHotkeyManager(handle);
        _hotkeys.Pressed += Hotkeys_Pressed;
        if (!_hotkeys.RegisterDefaults())
        {
            StatusDetailText.Text = "One or more global shortcuts are already in use. The on-screen controls still work.";
        }

        _trayIcon = new TrayIconController(
            show: () => QueueUiAction(() =>
            {
                ShowFromTray();
                return Task.CompletedTask;
            }, "The window could not be shown"),
            selectTarget: () => QueueUiAction(() =>
            {
                ShowFromTray();
                WindowTargetCombo.Focus();
                return Task.CompletedTask;
            }, "Target selection could not be shown"),
            shadowMode: () => QueueUiAction(
                () => SetDetectionModeFromTrayAsync(automatic: false),
                "Shadow mode could not be enabled"),
            automaticMode: () => QueueUiAction(
                () => SetDetectionModeFromTrayAsync(automatic: true),
                "Automatic mode could not be enabled"),
            pause: () => QueueUiAction(PauseDetectionAsync, "Detection could not be paused safely"),
            emergencyUnmute: () => QueueUiAction(EmergencyUnmuteAsync, "Emergency unmute failed"),
            exit: () => QueueUiAction(ExitFromTrayAsync, "LLM AdShield could not finish shutting down"));

        await RecoverAudioAsync();
        InitializeProviderSettings();
        RefreshWindows();
        RefreshAudio();
    }

    private void InitializeProviderSettings()
    {
        ProviderCombo.ItemsSource = LlmProviders.Options;
        var preferredProvider = _providerPreferences.Load();
        ProviderCombo.SelectedItem = LlmProviders.Options.First(option => option.Provider == preferredProvider);
        UpdateProviderSettingsUi(preferredProvider);
    }

    private void ProviderCombo_SelectionChanged(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ProviderCombo.SelectedItem is not LlmProviderOption option)
        {
            return;
        }

        try
        {
            _providerPreferences.Save(option.Provider);
            ProviderApiKeyBox.Clear();
            UpdateProviderSettingsUi(option.Provider);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ProviderKeyStatusText.Text = $"The provider choice could not be saved: {exception.Message}";
        }
    }

    private void SaveProviderKeyButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProviderCombo.SelectedItem is not LlmProviderOption option)
        {
            ProviderKeyStatusText.Text = "Choose a provider before saving a key.";
            return;
        }

        if (string.IsNullOrWhiteSpace(ProviderApiKeyBox.Password))
        {
            ProviderKeyStatusText.Text = "Enter an API key first. No network request was sent.";
            return;
        }

        try
        {
            _credentialStore.Save(option.Provider, ProviderApiKeyBox.Password);
            ProviderApiKeyBox.Clear();
            UpdateProviderSettingsUi(option.Provider);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception)
        {
            ProviderKeyStatusText.Text = $"Windows could not save the key securely: {exception.Message}";
        }
    }

    private void RemoveProviderKeyButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProviderCombo.SelectedItem is not LlmProviderOption option)
        {
            return;
        }

        try
        {
            _credentialStore.Remove(option.Provider);
            ProviderApiKeyBox.Clear();
            UpdateProviderSettingsUi(option.Provider);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ProviderKeyStatusText.Text = $"The saved key could not be removed: {exception.Message}";
        }
    }

    private void UpdateProviderSettingsUi(LlmProvider provider)
    {
        var option = LlmProviders.Get(provider);
        var hasStoredKey = _credentialStore.HasStoredCredential(provider);
        var hasEnvironmentKey = !string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable(option.EnvironmentVariableName));
        ProviderKeyStatusText.Text = hasStoredKey
            ? $"{option.DisplayName} key saved with Windows DPAPI for this user. It has not been network-tested."
            : hasEnvironmentKey
                ? $"Using {option.EnvironmentVariableName}; no key is saved by LLM AdShield."
                : $"No {option.DisplayName} key is configured. Save one above or set {option.EnvironmentVariableName}.";
        RemoveProviderKeyButton.IsEnabled = hasStoredKey && _coordinator is null;

        var models = AdClassifierFactory.GetModels(provider, _settings);
        ProviderModelsText.Text = models.Primary == models.Escalation
            ? $"Provider: {option.DisplayName}  •  Model: {models.Primary}"
            : $"Primary: {models.Primary}  •  Escalation: {models.Escalation}";
        ProviderPrivacyText.Text =
            $"Three resized frames from the selected player are sent to {option.DisplayName} per decision. Locally, frames remain in RAM and are never written to disk; audio is never uploaded.";
        ProviderRetentionText.Text = provider == LlmProvider.OpenAi
            ? "OpenAI requests use store:false. Provider abuse-monitoring and retention terms still apply."
            : "Claude requests use the Messages API with structured JSON output. Anthropic API data-control and retention terms apply.";
    }

    private async Task RecoverAudioAsync()
    {
        var recovery = await WindowsAudioMuteLease.RecoverStaleLeaseAsync();
        if (recovery.Status == AudioMuteRecoveryStatus.Restored)
        {
            StatusDetailText.Text = $"Recovered {recovery.RestoredSessionCount} audio session(s) from an interrupted LLM AdShield run.";
        }
        else if (recovery.Status == AudioMuteRecoveryStatus.Failed || recovery.MissingSessionCount > 0)
        {
            StatusDetailText.Text = "A prior audio lease could not be fully restored. Its recovery record was retained; check the original session before continuing.";
        }
    }

    private void RefreshWindowsButton_Click(object sender, RoutedEventArgs e) => RefreshWindows();

    private void RefreshAudioButton_Click(object sender, RoutedEventArgs e) => RefreshAudio();

    private void WindowTargetCombo_SelectionChanged(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (IsLoaded && _coordinator is null)
        {
            RefreshAudio();
        }
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e) =>
        await RunUiActionAsync(StartDetectionAsync, "LLM AdShield could not start safely", showDialog: true);

    private async Task StartDetectionAsync()
    {
        if (_coordinator is not null)
        {
            await _coordinator.SetAutomaticModeAsync(AutomaticModeRadio.IsChecked == true);
            return;
        }

        if (WindowTargetCombo.SelectedItem is not CaptureTarget target)
        {
            throw new InvalidOperationException("Select the dedicated IPTV player window first.");
        }

        if (AudioSessionCombo.SelectedItem is not AudioSessionInfo session)
        {
            throw new InvalidOperationException("Select the player audio session first. Start playback, then click Refresh if it is not listed.");
        }

        if (!WindowCaptureCatalog.IsSameWindow(target))
        {
            throw new InvalidOperationException("The selected player window changed. Refresh the list and select it again.");
        }

        if (ProviderCombo.SelectedItem is not LlmProviderOption providerOption)
        {
            throw new InvalidOperationException("Choose an LLM provider first.");
        }

        var credential = _credentialResolver.Resolve(providerOption.Provider);
        if (!credential.IsConfigured)
        {
            throw new InvalidOperationException(
                $"No {providerOption.DisplayName} API key is configured. Save one in Provider settings or set {providerOption.EnvironmentVariableName}.");
        }

        ICaptureSource? capture = null;
        IAudioMuteLease? audioLease = null;
        IOverlayPresenter? overlay = null;
        JsonlTelemetryLogger? logger = null;
        AdClassifierSet? classifiers = null;
        var resourcesTransferred = false;

        try
        {
            capture = new WindowsGraphicsCaptureSource(target);
            var selection = new AudioSessionSelection([session.SessionInstanceId]);
            audioLease = new WindowsAudioMuteLease(selection, _audioEnumerator);
            overlay = new WpfOverlayPresenter(target, Dispatcher);
            logger = new JsonlTelemetryLogger(_settings.TelemetryDirectory);
            classifiers = AdClassifierFactory.Create(
                providerOption.Provider,
                credential.ApiKey!,
                _settings);

            var coordinator = new DetectionCoordinator(
                _settings,
                capture,
                classifiers.Primary,
                classifiers.Escalation,
                new BalancedDetectionStateMachine(),
                audioLease,
                overlay,
                logger,
                automaticMode: AutomaticModeRadio.IsChecked == true,
                classifierCosts: classifiers.CostProfile);
            _coordinator = coordinator;
            resourcesTransferred = true;
            _coordinator.StatusChanged += Coordinator_StatusChanged;
            await _coordinator.StartAsync();

            Interlocked.Exchange(ref _terminalCleanupStarted, 0);
            SetControlsRunning(true);
            StatusDetailText.Text = $"Metadata log: {_coordinator.TelemetryPath}";
        }
        catch (Exception startupException)
        {
            Exception? cleanupException = null;
            try
            {
                if (resourcesTransferred)
                {
                    await DisposeCoordinatorAsync();
                }
                else
                {
                    await DisposeStartupResourcesAsync(capture, audioLease, overlay, logger, classifiers);
                }
            }
            catch (Exception exception)
            {
                cleanupException = exception;
            }

            SetControlsRunning(false);
            if (cleanupException is not null)
            {
                throw new AggregateException(
                    "LLM AdShield could not start and one or more partially-created resources could not be released.",
                    startupException,
                    cleanupException);
            }

            throw;
        }
    }

    private async void PauseButton_Click(object sender, RoutedEventArgs e) =>
        await RunUiActionAsync(PauseDetectionAsync, "Detection could not be paused safely", showDialog: true);

    private async void EmergencyButton_Click(object sender, RoutedEventArgs e) =>
        await RunUiActionAsync(EmergencyUnmuteAsync, "Emergency unmute failed", showDialog: true);

    private async void DetectionMode_Checked(object sender, RoutedEventArgs e)
    {
        _trayIcon?.SetAutomaticMode(AutomaticModeRadio.IsChecked == true);
        if (_suppressModeEvent || _coordinator is null)
        {
            return;
        }

        await RunUiActionAsync(
            () => _coordinator is null
                ? Task.CompletedTask
                : _coordinator.SetAutomaticModeAsync(AutomaticModeRadio.IsChecked == true),
            "The mode could not be changed safely",
            showDialog: true);
    }

    private async Task SetDetectionModeFromTrayAsync(bool automatic)
    {
        _suppressModeEvent = true;
        try
        {
            if (automatic)
            {
                AutomaticModeRadio.IsChecked = true;
            }
            else
            {
                ShadowModeRadio.IsChecked = true;
            }
        }
        finally
        {
            _suppressModeEvent = false;
        }

        _trayIcon?.SetAutomaticMode(automatic);
        if (_coordinator is not null)
        {
            await _coordinator.SetAutomaticModeAsync(automatic);
        }
    }

    private void RefreshWindows()
    {
        var selectedHandle = (WindowTargetCombo.SelectedItem as CaptureTarget)?.WindowHandle;
        var targets = WindowCaptureCatalog.Enumerate();
        WindowTargetCombo.ItemsSource = targets;
        WindowTargetCombo.SelectedItem = selectedHandle is null
            ? null
            : targets.FirstOrDefault(target => target.WindowHandle == selectedHandle.Value);
    }

    private void RefreshAudio()
    {
        try
        {
            var selectedId = (AudioSessionCombo.SelectedItem as AudioSessionInfo)?.SessionInstanceId;
            var targetProcessId = (WindowTargetCombo.SelectedItem as CaptureTarget)?.ProcessId;
            var sessions = _audioEnumerator.Enumerate()
                .Where(session => !session.IsSystemSoundsSession && session.State != AudioSessionActivityState.Expired)
                .OrderByDescending(session =>
                    targetProcessId is not null && session.ProcessId == unchecked((uint)targetProcessId.Value))
                .ThenByDescending(session => session.State == AudioSessionActivityState.Active)
                .ThenBy(session => session.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            AudioSessionCombo.ItemsSource = sessions;
            AudioSessionCombo.SelectedItem = selectedId is null
                ? null
                : sessions.FirstOrDefault(session => session.SessionInstanceId == selectedId);
            StatusDetailText.Text = sessions.Length == 0
                ? "No application audio sessions are available. Start audible playback, wait a moment, then click Refresh."
                : $"Found {sessions.Length} application audio session(s). Select the player session explicitly; likely window matches are listed first.";
        }
        catch (Exception exception)
        {
            AudioSessionCombo.ItemsSource = null;
            AudioSessionCombo.SelectedItem = null;
            StatusDetailText.Text = $"Audio sessions could not be enumerated: {exception.Message}";
        }
    }

    private async void Hotkeys_Pressed(object? sender, LLMAdShieldHotkey hotkey)
    {
        try
        {
            switch (hotkey)
            {
                case LLMAdShieldHotkey.MarkAdvertisement when _coordinator is not null:
                    await _coordinator.AnnotateAsync(ManualAnnotationLabel.Advertisement);
                    StatusDetailText.Text = "Manual annotation saved: advertisement.";
                    break;
                case LLMAdShieldHotkey.MarkProgram when _coordinator is not null:
                    await _coordinator.AnnotateAsync(ManualAnnotationLabel.Program);
                    StatusDetailText.Text = "Manual annotation saved: program.";
                    break;
                case LLMAdShieldHotkey.EmergencyUnmute:
                    await EmergencyUnmuteAsync();
                    break;
            }
        }
        catch (Exception exception)
        {
            StatusDetailText.Text = $"Shortcut action failed safely: {exception.Message}";
        }
    }

    private void Coordinator_StatusChanged(object? sender, CoordinatorStatus status) =>
        _ = DispatchStatusAsync(status);

    private async Task DispatchStatusAsync(CoordinatorStatus status)
    {
        try
        {
            await Dispatcher.InvokeAsync(() => UpdateStatus(status));
        }
        catch (TaskCanceledException)
        {
        }
        catch (InvalidOperationException) when (Dispatcher.HasShutdownStarted)
        {
        }
    }

    private void UpdateStatus(CoordinatorStatus status)
    {
        RunStateText.Text = status.Phase switch
        {
            CoordinatorPhase.Muted => "Ad muted",
            CoordinatorPhase.ShadowAdvertisement => "Ad detected (shadow)",
            CoordinatorPhase.Classifying => "Classifying",
            CoordinatorPhase.Capturing => "Watching",
            CoordinatorPhase.BudgetExhausted => "Budget paused",
            CoordinatorPhase.SourceUnavailable => "Source unavailable",
            CoordinatorPhase.Faulted => "Faulted safely",
            CoordinatorPhase.Paused => "Paused",
            CoordinatorPhase.Stopped => "Stopped",
            _ => "Starting",
        };

        var budgetWarning = status.Budget.Level == BudgetLevel.Warning;
        BudgetWarningText.Visibility = budgetWarning ? Visibility.Visible : Visibility.Collapsed;
        SpendText.Foreground = status.Budget.Level switch
        {
            BudgetLevel.Warning => new SolidColorBrush(System.Windows.Media.Color.FromRgb(245, 213, 138)),
            BudgetLevel.Exhausted => new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 145, 135)),
            _ => Foreground,
        };
        _trayIcon?.SetStatus(budgetWarning ? $"{RunStateText.Text}; budget warning" : RunStateText.Text);
        StatusDetailText.Text = status.Detail;
        SpendText.Text = $"{status.Budget.SpentUsd:C2} / {status.Budget.HardCapUsd:C2}";

        if (status.Classification is { } result)
        {
            LastVerdictText.Text = result.Verdict?.CurrentLabel.ToString() ?? result.Outcome.ToString();
            ConfidenceText.Text = result.Verdict?.Confidence.ToString() ?? "—";
            LatencyText.Text = $"{result.Latency.TotalMilliseconds:F0} ms";
        }

        if (status.Phase is CoordinatorPhase.BudgetExhausted or CoordinatorPhase.Faulted or CoordinatorPhase.SourceUnavailable &&
            Interlocked.Exchange(ref _terminalCleanupStarted, 1) == 0)
        {
            QueueUiAction(FinishTerminalRunAsync, "The stopped run could not be fully cleaned up");
        }
    }

    private async Task FinishTerminalRunAsync()
    {
        try
        {
            await DisposeCoordinatorAsync();
        }
        finally
        {
            SetControlsRunning(false);
        }
    }

    private async Task PauseDetectionAsync()
    {
        try
        {
            await DisposeCoordinatorAsync();
        }
        finally
        {
            SetControlsRunning(false);
            RunStateText.Text = "Stopped";
            _trayIcon?.SetStatus("stopped");
            StatusDetailText.Text = "Detection stopped and the original audio state was restored.";
        }
    }

    private async Task EmergencyUnmuteAsync()
    {
        ShadowModeRadio.IsChecked = true;
        if (_coordinator is not null)
        {
            await _coordinator.EmergencyUnmuteAsync();
        }
        else
        {
            await WindowsAudioMuteLease.RecoverStaleLeaseAsync();
        }
    }

    private static async Task DisposeStartupResourcesAsync(
        ICaptureSource? capture,
        IAudioMuteLease? audioLease,
        IOverlayPresenter? overlay,
        JsonlTelemetryLogger? logger,
        AdClassifierSet? classifiers)
    {
        var errors = new List<Exception>();
        await TryDisposeAsync(capture, errors);
        await TryDisposeAsync(audioLease, errors);

        if (overlay is IAsyncDisposable asyncOverlay)
        {
            await TryDisposeAsync(asyncOverlay, errors);
        }
        else
        {
            TryDispose(overlay as IDisposable, errors);
        }

        if (classifiers is not null)
        {
            TryDispose(classifiers.Primary as IDisposable, errors);
            if (!ReferenceEquals(classifiers.Primary, classifiers.Escalation))
            {
                TryDispose(classifiers.Escalation as IDisposable, errors);
            }
        }

        await TryDisposeAsync(logger, errors);

        if (errors.Count > 0)
        {
            throw new AggregateException(
                "One or more partially-created LLM AdShield resources could not be released.",
                errors);
        }
    }

    private static async ValueTask TryDisposeAsync(
        IAsyncDisposable? resource,
        ICollection<Exception> errors)
    {
        if (resource is null)
        {
            return;
        }

        try
        {
            await resource.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            errors.Add(exception);
        }
    }

    private static void TryDispose(IDisposable? resource, ICollection<Exception> errors)
    {
        if (resource is null)
        {
            return;
        }

        try
        {
            resource.Dispose();
        }
        catch (Exception exception)
        {
            errors.Add(exception);
        }
    }

    private async Task DisposeCoordinatorAsync()
    {
        var coordinator = Interlocked.Exchange(ref _coordinator, null);
        if (coordinator is null)
        {
            return;
        }

        coordinator.StatusChanged -= Coordinator_StatusChanged;
        await coordinator.DisposeAsync().ConfigureAwait(false);
    }

    private void SetControlsRunning(bool running)
    {
        StartButton.IsEnabled = !running;
        PauseButton.IsEnabled = running;
        WindowTargetCombo.IsEnabled = !running;
        AudioSessionCombo.IsEnabled = !running;
        RefreshWindowsButton.IsEnabled = !running;
        RefreshAudioButton.IsEnabled = !running;
        ProviderCombo.IsEnabled = !running;
        ProviderApiKeyBox.IsEnabled = !running;
        SaveProviderKeyButton.IsEnabled = !running;
        if (ProviderCombo.SelectedItem is LlmProviderOption option)
        {
            RemoveProviderKeyButton.IsEnabled = !running && _credentialStore.HasStoredCredential(option.Provider);
        }
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private async Task ExitFromTrayAsync()
    {
        await ShutdownAsync();
        Close();
        System.Windows.Application.Current.Shutdown();
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_shutdownStarted)
        {
            return;
        }

        e.Cancel = true;
        Hide();
        _trayIcon?.SetStatus("running in tray");
    }

    public Task ShutdownAsync()
    {
        lock (_shutdownSync)
        {
            _shutdownStarted = true;
            return _shutdownTask ??= ShutdownCoreAsync();
        }
    }

    private async Task ShutdownCoreAsync()
    {
        Exception? coordinatorError = null;
        try
        {
            await DisposeCoordinatorAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            coordinatorError = exception;
        }

        try
        {
            await Dispatcher.InvokeAsync(() =>
            {
                _hotkeys?.Dispose();
                _hotkeys = null;
                _trayIcon?.Dispose();
                _trayIcon = null;
            });
        }
        catch (TaskCanceledException)
        {
        }

        if (coordinatorError is not null)
        {
            throw new InvalidOperationException(
                "Detection stopped, but an audio session may still require manual restoration. The recovery journal was retained.",
                coordinatorError);
        }
    }

    private void QueueUiAction(Func<Task> action, string failurePrefix) =>
        _ = DispatchUiActionAsync(action, failurePrefix);

    private async Task DispatchUiActionAsync(Func<Task> action, string failurePrefix)
    {
        try
        {
            if (Dispatcher.CheckAccess())
            {
                await RunUiActionAsync(action, failurePrefix);
                return;
            }

            await Dispatcher.InvokeAsync(() => RunUiActionAsync(action, failurePrefix)).Task.Unwrap();
        }
        catch (Exception) when (Dispatcher.HasShutdownStarted)
        {
        }
    }

    private async Task RunUiActionAsync(Func<Task> action, string failurePrefix, bool showDialog = false)
    {
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            var message = $"{failurePrefix}: {exception.Message}";
            StatusDetailText.Text = message;
            if (showDialog)
            {
                ShowError(message);
            }
        }
    }

    private static void ShowError(string message) =>
        System.Windows.MessageBox.Show(
            message,
            "LLM AdShield",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
}
