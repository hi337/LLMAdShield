namespace LLMAdShield.Core;

public sealed record CaptureSourceDescriptor(string Id, string DisplayName);

public interface ICaptureSource : IAsyncDisposable
{
    CaptureSourceDescriptor Source { get; }

    IAsyncEnumerable<CapturedFrame> CaptureAsync(CancellationToken cancellationToken = default);
}

public interface IAdClassifier
{
    ValueTask<ClassificationResult> ClassifyAsync(
        FrameWindow frameWindow,
        CancellationToken cancellationToken = default);
}

public interface IDetectionStateMachine
{
    DetectionSnapshot Snapshot { get; }

    DetectionDecision Process(ClassificationResult result, DateTimeOffset now);

    DetectionDecision Tick(DateTimeOffset now);

    DetectionDecision ForceFailOpen(DateTimeOffset now, DetectionReason reason);
}

public interface IAudioMuteLease : IAsyncDisposable
{
    AudioMuteLeaseState State { get; }

    ValueTask AcquireOrRenewAsync(
        DateTimeOffset expiresAt,
        MuteReason reason,
        CancellationToken cancellationToken = default);

    ValueTask ReleaseAsync(
        AudioReleaseReason reason,
        CancellationToken cancellationToken = default);
}

public interface IOverlayPresenter
{
    bool IsVisible { get; }

    ValueTask ShowAsync(
        OverlayPresentation presentation,
        CancellationToken cancellationToken = default);

    ValueTask HideAsync(CancellationToken cancellationToken = default);
}

public interface IOverlayFaultSource
{
    event EventHandler<OverlayFaultEventArgs>? Faulted;
}

public sealed class OverlayFaultEventArgs(string errorCode) : EventArgs
{
    public string ErrorCode { get; } = errorCode;
}

public sealed record OverlayPresentation(string Title, string Message)
{
    public static OverlayPresentation AdvertisementMuted { get; } =
        new("Advertisement", "Audio muted");
}

public sealed record AudioMuteLeaseState(bool IsMuted, DateTimeOffset? ExpiresAt);

public enum MuteReason
{
    AdvertisementDetected,
}

public enum AudioReleaseReason
{
    LeaseExpired,
    ProgramResumed,
    PublicSafety,
    BudgetCap,
    EmergencyUnmute,
    SourceUnavailable,
    ManualStop,
    Shutdown,
    Failure,
}
