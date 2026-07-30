using System.Collections.ObjectModel;

namespace LLMAdShield.Windows.Platform.Audio;

/// <summary>
/// The activity state reported by the Windows Core Audio session manager.
/// </summary>
public enum AudioSessionActivityState
{
    Inactive = 0,
    Active = 1,
    Expired = 2,
}

/// <summary>
/// A selectable, per-application render session. This is never the endpoint's
/// master-volume control.
/// </summary>
public sealed record AudioSessionInfo(
    string EndpointId,
    bool IsDefaultEndpoint,
    string SessionIdentifier,
    string SessionInstanceId,
    uint ProcessId,
    string DisplayName,
    AudioSessionActivityState State,
    bool IsMuted,
    float Volume,
    bool IsSystemSoundsSession)
{
    public string SelectionDisplayName =>
        $"{DisplayName} — PID {ProcessId} — {State}" +
        (IsDefaultEndpoint ? " — default output" : string.Empty);
}

/// <summary>
/// An explicit Core Audio selection. Exact session-instance IDs take precedence:
/// when any are supplied, process IDs cannot widen the selection. Process-wide
/// selection is available only when no exact session-instance IDs are supplied.
/// An empty selection is rejected to prevent an accidental broad mute.
/// </summary>
public sealed class AudioSessionSelection
{
    public AudioSessionSelection(
        IEnumerable<string>? sessionInstanceIds = null,
        IEnumerable<uint>? processIds = null)
    {
        SessionInstanceIds = new ReadOnlyCollection<string>(
            (sessionInstanceIds ?? [])
                .Where(static id => !string.IsNullOrWhiteSpace(id))
                .Select(static id => id.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToArray());

        ProcessIds = new ReadOnlyCollection<uint>(
            (processIds ?? [])
                .Where(static processId => processId != 0)
                .Distinct()
                .ToArray());

        if (SessionInstanceIds.Count == 0 && ProcessIds.Count == 0)
        {
            throw new ArgumentException(
                "At least one audio session instance ID or non-zero process ID is required.");
        }
    }

    public IReadOnlyList<string> SessionInstanceIds { get; }

    public IReadOnlyList<uint> ProcessIds { get; }

    internal bool Matches(AudioSessionInfo session)
    {
        if (session.IsSystemSoundsSession || session.State == AudioSessionActivityState.Expired)
        {
            return false;
        }

        if (SessionInstanceIds.Count != 0)
        {
            return SessionInstanceIds.Contains(session.SessionInstanceId, StringComparer.Ordinal);
        }

        return ProcessIds.Contains(session.ProcessId);
    }
}

public sealed class AudioSessionNotFoundException : InvalidOperationException
{
    public AudioSessionNotFoundException()
        : base("None of the explicitly selected Core Audio sessions are currently available.")
    {
    }
}
