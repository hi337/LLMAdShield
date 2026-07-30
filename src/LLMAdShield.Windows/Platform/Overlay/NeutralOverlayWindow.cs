using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using LLMAdShield.Core;

namespace LLMAdShield.Windows.Platform.Overlay;

internal sealed class NeutralOverlayWindow : Window
{
    private const int ExtendedWindowStyleIndex = -20;
    private const int OwnerWindowIndex = -8;
    private const long ExtendedStyleTransparent = 0x00000020L;
    private const long ExtendedStyleToolWindow = 0x00000080L;
    private const long ExtendedStyleNoActivate = 0x08000000L;
    private const uint DisplayAffinityExcludeFromCapture = 0x00000011;
    private const int WindowMessageMouseActivate = 0x0021;
    private const int WindowMessageNonClientHitTest = 0x0084;
    private const int MouseActivateNoActivate = 3;
    private const int HitTestTransparent = -1;
    private const uint SetWindowPositionNoActivate = 0x0010;
    private const uint SetWindowPositionShowWindow = 0x0040;
    private const uint SetWindowPositionNoZOrder = 0x0004;

    private readonly TextBlock _title;
    private readonly TextBlock _message;
    private nint _windowHandle;
    private HwndSource? _source;

    public NeutralOverlayWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        Topmost = false;
        Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(14, 17, 20));
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;

        _title = new TextBlock
        {
            Foreground = System.Windows.Media.Brushes.White,
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI Semibold"),
            FontSize = 32,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        };

        _message = new TextBlock
        {
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(174, 181, 188)),
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
            FontSize = 19,
            Margin = new Thickness(0, 10, 0, 0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        };

        var textStack = new StackPanel
        {
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            Margin = new Thickness(40),
            MaxWidth = 680,
            Children =
            {
                _title,
                _message,
            },
        };

        Content = new Grid
        {
            Background = Background,
            Children = { textStack },
        };
    }

    public void InitializeInterop(nint ownerWindowHandle)
    {
        VerifyAccess();
        if (_windowHandle != nint.Zero)
        {
            return;
        }

        _windowHandle = new WindowInteropHelper(this).EnsureHandle();
        var extendedStyle = NativeMethods.GetWindowLongPtr(_windowHandle, ExtendedWindowStyleIndex).ToInt64();
        extendedStyle |= ExtendedStyleTransparent |
            ExtendedStyleToolWindow |
            ExtendedStyleNoActivate;
        SetWindowLongPtrChecked(
            _windowHandle,
            ExtendedWindowStyleIndex,
            new nint(extendedStyle));

        if (ownerWindowHandle == nint.Zero)
        {
            throw new ArgumentException("A player window is required as the overlay owner.", nameof(ownerWindowHandle));
        }

        SetWindowLongPtrChecked(_windowHandle, OwnerWindowIndex, ownerWindowHandle);

        if (!NativeMethods.SetWindowDisplayAffinity(_windowHandle, DisplayAffinityExcludeFromCapture))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "The advertisement overlay could not be excluded from screen capture.");
        }

        _source = HwndSource.FromHwnd(_windowHandle);
        _source?.AddHook(WindowProcedure);
    }

    public void SetPresentation(OverlayPresentation presentation)
    {
        ArgumentNullException.ThrowIfNull(presentation);
        VerifyAccess();
        _title.Text = presentation.Title;
        _message.Text = presentation.Message;
    }

    public void SetPhysicalBounds(Rect physicalBounds)
    {
        VerifyAccess();
        if (_windowHandle == nint.Zero)
        {
            throw new InvalidOperationException("Initialize the overlay before positioning it.");
        }

        var width = Math.Max(1, checked((int)Math.Round(physicalBounds.Width)));
        var height = Math.Max(1, checked((int)Math.Round(physicalBounds.Height)));
        var left = checked((int)Math.Round(physicalBounds.Left));
        var top = checked((int)Math.Round(physicalBounds.Top));

        if (!NativeMethods.SetWindowPos(
                _windowHandle,
                nint.Zero,
                left,
                top,
                width,
                height,
                SetWindowPositionNoActivate |
                SetWindowPositionShowWindow |
                SetWindowPositionNoZOrder))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The overlay could not be aligned to the player window.");
        }
    }

    private static void SetWindowLongPtrChecked(nint windowHandle, int index, nint value)
    {
        Marshal.SetLastPInvokeError(0);
        var previousValue = NativeMethods.SetWindowLongPtr(windowHandle, index, value);
        var error = Marshal.GetLastPInvokeError();
        if (previousValue == nint.Zero && error != 0)
        {
            throw new Win32Exception(error, "The advertisement overlay window style could not be configured.");
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_source is not null)
        {
            _source.RemoveHook(WindowProcedure);
            _source = null;
        }

        _windowHandle = nint.Zero;
        base.OnClosed(e);
    }

    private nint WindowProcedure(
        nint windowHandle,
        int message,
        nint wParam,
        nint lParam,
        ref bool handled)
    {
        if (message == WindowMessageMouseActivate)
        {
            handled = true;
            return new nint(MouseActivateNoActivate);
        }

        if (message == WindowMessageNonClientHitTest)
        {
            handled = true;
            return new nint(HitTestTransparent);
        }

        return nint.Zero;
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        internal static extern nint GetWindowLongPtr(nint windowHandle, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        internal static extern nint SetWindowLongPtr(nint windowHandle, int index, nint newValue);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetWindowDisplayAffinity(nint windowHandle, uint affinity);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetWindowPos(
            nint windowHandle,
            nint insertAfter,
            int left,
            int top,
            int width,
            int height,
            uint flags);
    }
}
