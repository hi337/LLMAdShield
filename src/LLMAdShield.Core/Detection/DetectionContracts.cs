namespace LLMAdShield.Core;

public sealed record DetectionPolicy
{
    public TimeSpan AdConfirmationWindow { get; init; } = TimeSpan.FromSeconds(4);

    public int MediumConfidenceConfirmationsRequired { get; init; } = 2;

    public int ProgramConfirmationsRequired { get; init; } = 3;

    public int ProgramConfirmationsWithoutAdEndRequired { get; init; } = 5;

    public TimeSpan MuteLeaseDuration { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan MinimumMuteDuration { get; init; } = TimeSpan.FromSeconds(20);

    public TimeSpan MaximumResultAge { get; init; } = TimeSpan.FromSeconds(3);

    public static DetectionPolicy Balanced { get; } = new();

    internal void Validate()
    {
        if (AdConfirmationWindow <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(AdConfirmationWindow));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(MediumConfidenceConfirmationsRequired, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(ProgramConfirmationsRequired, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(
            ProgramConfirmationsWithoutAdEndRequired,
            ProgramConfirmationsRequired);

        if (MuteLeaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(MuteLeaseDuration));
        }

        if (MinimumMuteDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(MinimumMuteDuration));
        }

        if (MaximumResultAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumResultAge));
        }
    }
}

public enum DetectionAction
{
    None,
    AcquireOrRenewMute,
    ReleaseMute,
}

public enum DetectionReason
{
    NoChange,
    HighConfidenceAdvertisement,
    RepeatedAdvertisementEvidence,
    ConfirmedAdvertisementRenewal,
    MinimumMuteHold,
    ProgramResumed,
    PublicSafety,
    LeaseExpired,
    StaleClassification,
    ClassificationFailure,
    UnknownContent,
    LowConfidence,
    BudgetCap,
    EmergencyUnmute,
    SourceUnavailable,
    ManualStop,
    Shutdown,
}

public sealed record DetectionSnapshot(
    bool IsMuted,
    bool IsAdvertisementSuspected,
    DateTimeOffset? MuteLeaseExpiresAt,
    int RecentAdvertisementConfirmations,
    int ConsecutiveProgramVerdicts);

public sealed record DetectionDecision(
    DetectionAction Action,
    DetectionReason Reason,
    DateTimeOffset DecidedAt,
    DetectionSnapshot Snapshot);
