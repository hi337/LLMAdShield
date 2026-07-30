using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace LLMAdShield.Windows.Platform.Hotkeys;

internal enum LLMAdShieldHotkey
{
    MarkAdvertisement = 1,
    MarkProgram = 2,
    EmergencyUnmute = 3,
}

internal sealed class GlobalHotkeyManager : IDisposable
{
    private const int HotkeyMessage = 0x0312;
    private const uint ModifierAlt = 0x0001;
    private const uint ModifierControl = 0x0002;
    private const uint ModifierNoRepeat = 0x4000;

    private readonly nint _windowHandle;
    private readonly HwndSource _source;
    private readonly HashSet<int> _registered = [];
    private bool _disposed;

    public GlobalHotkeyManager(nint windowHandle)
    {
        _windowHandle = windowHandle;
        _source = HwndSource.FromHwnd(windowHandle)
            ?? throw new InvalidOperationException("The LLM AdShield window handle is unavailable.");
        _source.AddHook(WindowProcedure);
    }

    public event EventHandler<LLMAdShieldHotkey>? Pressed;

    public bool RegisterDefaults()
    {
        var success = true;
        success &= Register(LLMAdShieldHotkey.MarkAdvertisement, 0x41); // A
        success &= Register(LLMAdShieldHotkey.MarkProgram, 0x50); // P
        success &= Register(LLMAdShieldHotkey.EmergencyUnmute, 0x55); // U
        return success;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        foreach (var id in _registered)
        {
            _ = NativeMethods.UnregisterHotKey(_windowHandle, id);
        }

        _registered.Clear();
        _source.RemoveHook(WindowProcedure);
        _disposed = true;
    }

    private bool Register(LLMAdShieldHotkey hotkey, uint virtualKey)
    {
        var id = (int)hotkey;
        var registered = NativeMethods.RegisterHotKey(
            _windowHandle,
            id,
            ModifierControl | ModifierAlt | ModifierNoRepeat,
            virtualKey);
        if (registered)
        {
            _registered.Add(id);
        }

        return registered;
    }

    private nint WindowProcedure(
        nint windowHandle,
        int message,
        nint wParam,
        nint lParam,
        ref bool handled)
    {
        if (message == HotkeyMessage && Enum.IsDefined(typeof(LLMAdShieldHotkey), wParam.ToInt32()))
        {
            handled = true;
            Pressed?.Invoke(this, (LLMAdShieldHotkey)wParam.ToInt32());
        }

        return nint.Zero;
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool RegisterHotKey(nint windowHandle, int id, uint modifiers, uint virtualKey);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UnregisterHotKey(nint windowHandle, int id);
    }
}
