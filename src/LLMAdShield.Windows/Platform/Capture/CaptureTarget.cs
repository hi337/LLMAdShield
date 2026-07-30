using System.Windows;

namespace LLMAdShield.Windows.Platform.Capture;

/// <summary>
/// A visible top-level window that can be used as a Windows Graphics Capture target.
/// Process creation time prevents a recycled HWND/PID from silently retargeting LLMAdShield.
/// </summary>
public sealed record CaptureTarget(
    nint WindowHandle,
    string Title,
    int ProcessId,
    long ProcessStartTimeUtcTicks,
    string ProcessName,
    Rect Bounds)
{
    public string DisplayName => string.IsNullOrWhiteSpace(ProcessName)
        ? Title
        : $"{Title} — {ProcessName}";
}
