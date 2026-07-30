using System.Diagnostics;
using System.Runtime.Versioning;

namespace LLMAdShield.Windows.Platform.Audio;

/// <summary>
/// Enumerates per-application audio sessions across active Windows render endpoints.
/// </summary>
[SupportedOSPlatform("windows")]
public class WindowsAudioSessionEnumerator
{
    public IReadOnlyList<AudioSessionInfo> Enumerate()
    {
        EnsureWindows();

        var handles = EnumerateHandles();
        try
        {
            return handles.Select(static handle => handle.Info).ToArray();
        }
        finally
        {
            DisposeAll(handles);
        }
    }

    public ValueTask<IReadOnlyList<AudioSessionInfo>> EnumerateAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Enumerate());
    }

    internal virtual List<IAudioSessionHandle> EnumerateHandles()
    {
        EnsureWindows();

        IMMDeviceEnumerator? deviceEnumerator = null;
        IMMDeviceCollection? devices = null;
        IMMDevice? defaultDevice = null;
        var result = new List<IAudioSessionHandle>();

        try
        {
            // Cast through object so the runtime performs QueryInterface on the
            // COM coclass; the coclass declaration itself cannot name its COM
            // interfaces as managed base interfaces.
            object deviceEnumeratorObject = new MMDeviceEnumeratorComObject();
            deviceEnumerator = (IMMDeviceEnumerator)deviceEnumeratorObject;

            string? defaultEndpointId = null;
            var defaultResult = deviceEnumerator.GetDefaultAudioEndpoint(
                EDataFlow.Render,
                ERole.Multimedia,
                out defaultDevice);

            if (defaultResult >= 0)
            {
                CoreAudioInterop.ThrowIfFailed(
                    defaultDevice.GetId(out defaultEndpointId!),
                    "read the default render endpoint ID");
            }

            CoreAudioInterop.ThrowIfFailed(
                deviceEnumerator.EnumAudioEndpoints(
                    EDataFlow.Render,
                    DeviceState.Active,
                    out devices),
                "enumerate active render endpoints");

            CoreAudioInterop.ThrowIfFailed(devices.GetCount(out var deviceCount), "count render endpoints");

            for (uint deviceIndex = 0; deviceIndex < deviceCount; deviceIndex++)
            {
                IMMDevice? device = null;
                try
                {
                    CoreAudioInterop.ThrowIfFailed(devices.Item(deviceIndex, out device), "open a render endpoint");
                    CoreAudioInterop.ThrowIfFailed(device.GetId(out var endpointId), "read a render endpoint ID");

                    EnumerateEndpointSessions(
                        device,
                        endpointId,
                        string.Equals(endpointId, defaultEndpointId, StringComparison.Ordinal),
                        result);
                }
                finally
                {
                    CoreAudioInterop.Release(device);
                }
            }

            return result;
        }
        catch
        {
            DisposeAll(result);
            throw;
        }
        finally
        {
            CoreAudioInterop.Release(defaultDevice);
            CoreAudioInterop.Release(devices);
            CoreAudioInterop.Release(deviceEnumerator);
        }
    }

    private static void EnumerateEndpointSessions(
        IMMDevice device,
        string endpointId,
        bool isDefaultEndpoint,
        ICollection<IAudioSessionHandle> destination)
    {
        object? managerObject = null;
        IAudioSessionEnumeratorNative? sessions = null;

        try
        {
            var managerInterfaceId = CoreAudioInterop.AudioSessionManager2InterfaceId;
            CoreAudioInterop.ThrowIfFailed(
                device.Activate(
                    ref managerInterfaceId,
                    ClsContext.All,
                    IntPtr.Zero,
                    out managerObject),
                "activate the per-session audio manager");

            var manager = (IAudioSessionManager2Native)managerObject;
            CoreAudioInterop.ThrowIfFailed(manager.GetSessionEnumerator(out sessions), "enumerate audio sessions");
            CoreAudioInterop.ThrowIfFailed(sessions.GetCount(out var count), "count audio sessions");

            for (var sessionIndex = 0; sessionIndex < count; sessionIndex++)
            {
                IAudioSessionControlNative? control = null;
                try
                {
                    CoreAudioInterop.ThrowIfFailed(
                        sessions.GetSession(sessionIndex, out control),
                        "open an audio session");

                    if (control is not IAudioSessionControl2Native control2
                        || control is not ISimpleAudioVolumeNative volume)
                    {
                        CoreAudioInterop.Release(control);
                        control = null;
                        continue;
                    }

                    var handle = CreateHandle(
                        control,
                        control2,
                        volume,
                        endpointId,
                        isDefaultEndpoint);

                    control = null; // Ownership moved to CoreAudioSessionHandle.
                    if (handle.Info.State == AudioSessionActivityState.Expired)
                    {
                        handle.Dispose();
                        continue;
                    }

                    destination.Add(handle);
                }
                finally
                {
                    CoreAudioInterop.Release(control);
                }
            }
        }
        finally
        {
            CoreAudioInterop.Release(sessions);
            CoreAudioInterop.Release(managerObject);
        }
    }

    private static CoreAudioSessionHandle CreateHandle(
        IAudioSessionControlNative lifetimeOwner,
        IAudioSessionControl2Native control,
        ISimpleAudioVolumeNative volume,
        string endpointId,
        bool isDefaultEndpoint)
    {
        CoreAudioInterop.ThrowIfFailed(control.GetState(out var nativeState), "read audio session state");
        CoreAudioInterop.ThrowIfFailed(control.GetSessionIdentifier(out var sessionId), "read audio session ID");
        CoreAudioInterop.ThrowIfFailed(
            control.GetSessionInstanceIdentifier(out var sessionInstanceId),
            "read audio session instance ID");
        CoreAudioInterop.ThrowIfFailed(control.GetProcessId(out var processId), "read audio session process ID");

        var systemResult = control.IsSystemSoundsSession();
        CoreAudioInterop.ThrowIfFailed(systemResult, "identify the Windows system-sounds session");
        var isSystemSounds = systemResult == 0;

        var displayResult = control.GetDisplayName(out var displayName);
        if (displayResult < 0 || string.IsNullOrWhiteSpace(displayName))
        {
            displayName = GetProcessDisplayName(processId, isSystemSounds);
        }

        CoreAudioInterop.ThrowIfFailed(volume.GetMute(out var isMuted), "read per-session mute state");
        CoreAudioInterop.ThrowIfFailed(volume.GetMasterVolume(out var sessionVolume), "read per-session volume");

        var info = new AudioSessionInfo(
            endpointId,
            isDefaultEndpoint,
            sessionId,
            sessionInstanceId,
            processId,
            displayName,
            (AudioSessionActivityState)nativeState,
            isMuted,
            sessionVolume,
            isSystemSounds);

        return new CoreAudioSessionHandle(lifetimeOwner, volume, info);
    }

    private static string GetProcessDisplayName(uint processId, bool isSystemSounds)
    {
        if (isSystemSounds)
        {
            return "Windows system sounds";
        }

        if (processId == 0 || processId > int.MaxValue)
        {
            return $"Process {processId}";
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            var description = process.MainModule?.FileVersionInfo.FileDescription;
            if (!string.IsNullOrWhiteSpace(description))
            {
                return description;
            }

            return process.ProcessName;
        }
        catch
        {
            return $"Process {processId}";
        }
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Core Audio session control is available only on Windows.");
        }
    }

    internal static void DisposeAll(IEnumerable<IAudioSessionHandle> handles)
    {
        foreach (var handle in handles)
        {
            handle.Dispose();
        }
    }
}

internal interface IAudioSessionHandle : IDisposable
{
    AudioSessionInfo Info { get; }

    bool GetMute();

    void SetMute(bool muted);
}

internal sealed class CoreAudioSessionHandle : IAudioSessionHandle
{
    private IAudioSessionControlNative? _lifetimeOwner;
    private ISimpleAudioVolumeNative? _volume;

    internal CoreAudioSessionHandle(
        IAudioSessionControlNative lifetimeOwner,
        ISimpleAudioVolumeNative volume,
        AudioSessionInfo info)
    {
        _lifetimeOwner = lifetimeOwner;
        _volume = volume;
        Info = info;
    }

    public AudioSessionInfo Info { get; }

    public bool GetMute()
    {
        var volume = _volume ?? throw new ObjectDisposedException(nameof(CoreAudioSessionHandle));
        CoreAudioInterop.ThrowIfFailed(volume.GetMute(out var muted), "read per-session mute state");
        return muted;
    }

    public void SetMute(bool muted)
    {
        var volume = _volume ?? throw new ObjectDisposedException(nameof(CoreAudioSessionHandle));
        var eventContext = CoreAudioInterop.EventContext;
        CoreAudioInterop.ThrowIfFailed(
            volume.SetMute(muted, ref eventContext),
            muted ? "mute an application audio session" : "unmute an application audio session");
    }

    public void Dispose()
    {
        var lifetimeOwner = Interlocked.Exchange(ref _lifetimeOwner, null);
        _volume = null;

        // The interface casts share one COM callable wrapper. Releasing its
        // IAudioSessionControl view releases all views without a double-release.
        CoreAudioInterop.Release(lifetimeOwner);
    }
}
