using System.Windows.Threading;
using LLMAdShield.Core;
using LLMAdShield.Windows.Platform.Capture;

namespace LLMAdShield.Windows.Platform.Overlay;

/// <summary>
/// Presents a capture-excluded, click-through neutral cover over a target HWND.
/// </summary>
public sealed class WpfOverlayPresenter : IOverlayPresenter, IOverlayFaultSource, IAsyncDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly CaptureTarget _target;
    private NeutralOverlayWindow? _window;
    private DispatcherTimer? _alignmentTimer;
    private int _requestedVisible;
    private bool _disposed;

    public WpfOverlayPresenter(CaptureTarget target, Dispatcher? dispatcher = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        _target = target;
        _dispatcher = dispatcher ?? System.Windows.Application.Current?.Dispatcher
            ?? Dispatcher.CurrentDispatcher;
    }

    public event EventHandler<OverlayFaultEventArgs>? Faulted;

    public bool IsVisible => Volatile.Read(ref _requestedVisible) != 0;

    public nint TargetWindowHandle => _target.WindowHandle;

    public ValueTask ShowAsync(
        OverlayPresentation presentation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(presentation);
        ObjectDisposedException.ThrowIf(_disposed, this);

        return InvokeOnDispatcherAsync(() =>
        {
            if (!WindowCaptureCatalog.IsSameWindow(_target))
            {
                throw new InvalidOperationException("The selected player window is no longer the original target.");
            }

            EnsureWindow();
            _window!.SetPresentation(presentation);
            Volatile.Write(ref _requestedVisible, 1);
            _alignmentTimer!.Start();
            AlignToTarget();
        }, cancellationToken);
    }

    public ValueTask HideAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return InvokeOnDispatcherAsync(() =>
        {
            Volatile.Write(ref _requestedVisible, 0);
            _alignmentTimer?.Stop();
            _window?.Hide();
        }, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Volatile.Write(ref _requestedVisible, 0);

        await InvokeOnDispatcherAsync(() =>
        {
            _alignmentTimer?.Stop();
            if (_alignmentTimer is not null)
            {
                _alignmentTimer.Tick -= OnAlignmentTimerTick;
                _alignmentTimer = null;
            }

            _window?.Close();
            _window = null;
        }, CancellationToken.None).ConfigureAwait(false);
    }

    private void EnsureWindow()
    {
        _dispatcher.VerifyAccess();
        if (_window is not null)
        {
            return;
        }

        _window = new NeutralOverlayWindow();
        _window.InitializeInterop(_target.WindowHandle);

        _alignmentTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(100),
            DispatcherPriority.Render,
            OnAlignmentTimerTick,
            _dispatcher);
        _alignmentTimer.Stop();
    }

    private void OnAlignmentTimerTick(object? sender, EventArgs eventArgs)
    {
        if (!IsVisible)
        {
            return;
        }

        try
        {
            AlignToTarget();
        }
        catch (Exception exception)
        {
            Volatile.Write(ref _requestedVisible, 0);
            _alignmentTimer?.Stop();
            _window?.Hide();
            Faulted?.Invoke(this, new OverlayFaultEventArgs(exception.GetType().Name));
        }
    }

    private void AlignToTarget()
    {
        _dispatcher.VerifyAccess();
        if (_window is null)
        {
            return;
        }

        if (!WindowCaptureCatalog.TryGetBounds(_target, out var bounds))
        {
            _window.Hide();
            return;
        }

        if (!_window.IsVisible)
        {
            _window.Show();
        }

        _window.SetPhysicalBounds(bounds);
    }

    private ValueTask InvokeOnDispatcherAsync(Action action, CancellationToken cancellationToken)
    {
        if (_dispatcher.CheckAccess())
        {
            cancellationToken.ThrowIfCancellationRequested();
            action();
            return ValueTask.CompletedTask;
        }

        return new ValueTask(_dispatcher
            .InvokeAsync(action, DispatcherPriority.Send, cancellationToken)
            .Task);
    }
}
