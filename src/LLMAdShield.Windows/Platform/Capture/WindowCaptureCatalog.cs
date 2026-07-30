using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace LLMAdShield.Windows.Platform.Capture;

/// <summary>
/// Enumerates ordinary, visible top-level windows without activating them.
/// </summary>
public static class WindowCaptureCatalog
{
    public static IReadOnlyList<CaptureTarget> Enumerate()
    {
        var ownProcessId = Environment.ProcessId;
        var results = new List<CaptureTarget>();

        _ = NativeMethods.EnumWindows((windowHandle, parameter) =>
        {
            if (!IsCandidate(windowHandle, ownProcessId))
            {
                return true;
            }

            var title = ReadWindowTitle(windowHandle);
            if (string.IsNullOrWhiteSpace(title) || !TryGetBounds(windowHandle, out var bounds))
            {
                return true;
            }

            _ = NativeMethods.GetWindowThreadProcessId(windowHandle, out var processId);
            var processInfo = ReadProcessInfo(unchecked((int)processId));
            results.Add(new CaptureTarget(
                windowHandle,
                title.Trim(),
                unchecked((int)processId),
                processInfo.StartTimeUtcTicks,
                processInfo.Name,
                bounds));

            return true;
        }, nint.Zero);

        return results
            .OrderBy(target => target.ProcessName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(target => target.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public static bool TryGetBounds(nint windowHandle, out Rect bounds)
    {
        bounds = Rect.Empty;
        if (windowHandle == nint.Zero ||
            !NativeMethods.IsWindow(windowHandle) ||
            !NativeMethods.IsWindowVisible(windowHandle) ||
            NativeMethods.IsIconic(windowHandle))
        {
            return false;
        }

        NativeMethods.RECT nativeBounds;
        var result = NativeMethods.DwmGetWindowAttribute(
            windowHandle,
            NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS,
            out nativeBounds,
            Marshal.SizeOf<NativeMethods.RECT>());

        if (result < 0 && !NativeMethods.GetWindowRect(windowHandle, out nativeBounds))
        {
            return false;
        }

        var width = nativeBounds.Right - nativeBounds.Left;
        var height = nativeBounds.Bottom - nativeBounds.Top;
        if (width <= 1 || height <= 1)
        {
            return false;
        }

        bounds = new Rect(nativeBounds.Left, nativeBounds.Top, width, height);
        return true;
    }

    public static bool Exists(nint windowHandle) =>
        windowHandle != nint.Zero && NativeMethods.IsWindow(windowHandle);

    public static bool IsSameWindow(CaptureTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!Exists(target.WindowHandle))
        {
            return false;
        }

        _ = NativeMethods.GetWindowThreadProcessId(target.WindowHandle, out var processId);
        if (processId != unchecked((uint)target.ProcessId))
        {
            return false;
        }

        if (target.ProcessStartTimeUtcTicks == 0)
        {
            return true;
        }

        return ReadProcessInfo(target.ProcessId).StartTimeUtcTicks == target.ProcessStartTimeUtcTicks;
    }

    public static bool TryGetBounds(CaptureTarget target, out Rect bounds)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!IsSameWindow(target))
        {
            bounds = Rect.Empty;
            return false;
        }

        return TryGetBounds(target.WindowHandle, out bounds);
    }

    private static bool IsCandidate(nint windowHandle, int ownProcessId)
    {
        if (!NativeMethods.IsWindowVisible(windowHandle) || NativeMethods.IsIconic(windowHandle))
        {
            return false;
        }

        _ = NativeMethods.GetWindowThreadProcessId(windowHandle, out var processId);
        if (processId == 0 || processId == ownProcessId)
        {
            return false;
        }

        var cloakResult = NativeMethods.DwmGetWindowAttribute(
            windowHandle,
            NativeMethods.DWMWA_CLOAKED,
            out uint cloaked,
            sizeof(uint));

        return cloakResult < 0 || cloaked == 0;
    }

    private static string ReadWindowTitle(nint windowHandle)
    {
        var length = NativeMethods.GetWindowTextLength(windowHandle);
        if (length <= 0)
        {
            return string.Empty;
        }

        var buffer = new StringBuilder(length + 1);
        _ = NativeMethods.GetWindowText(windowHandle, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private static (string Name, long StartTimeUtcTicks) ReadProcessInfo(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return (process.ProcessName, process.StartTime.ToUniversalTime().Ticks);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return (string.Empty, 0);
        }
    }

    private static class NativeMethods
    {
        internal const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
        internal const int DWMWA_CLOAKED = 14;

        internal delegate bool EnumWindowsProc(nint windowHandle, nint parameter);

        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT
        {
            internal int Left;
            internal int Top;
            internal int Right;
            internal int Bottom;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindow(nint windowHandle);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindowVisible(nint windowHandle);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsIconic(nint windowHandle);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern int GetWindowText(nint windowHandle, StringBuilder text, int maximumCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern int GetWindowTextLength(nint windowHandle);

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(nint windowHandle, out uint processId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetWindowRect(nint windowHandle, out RECT bounds);

        [DllImport("dwmapi.dll")]
        internal static extern int DwmGetWindowAttribute(
            nint windowHandle,
            int attribute,
            out RECT value,
            int valueSize);

        [DllImport("dwmapi.dll")]
        internal static extern int DwmGetWindowAttribute(
            nint windowHandle,
            int attribute,
            out uint value,
            int valueSize);
    }
}
