using System.IO;
using System.Runtime.Versioning;
using System.Threading;
using LLMAdShield.Core;

namespace LLMAdShield.Windows.Platform.Audio;

public enum AudioMuteRecoveryStatus
{
    NothingToRecover,
    Restored,
    ActiveLease,
    Failed,
}

public sealed record AudioMuteRecoveryResult(
    AudioMuteRecoveryStatus Status,
    int RestoredSessionCount,
    int MissingSessionCount,
    string? ErrorMessage = null);

/// <summary>
/// A crash-recoverable lease over explicitly selected per-application Core Audio
/// sessions. No endpoint/master-volume API is used by this type.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsAudioMuteLease : IAudioMuteLease
{
    private static readonly TimeSpan RestoreRetryDelay = TimeSpan.FromSeconds(1);

    private readonly WindowsAudioSessionEnumerator _sessionEnumerator;
    private readonly AudioSessionSelection _selection;
    private readonly AudioMuteRecoveryJournal _recoveryJournal;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly System.Threading.Timer _expiryTimer;

    private AudioMuteLeaseState _state = new(false, null);
    private AudioMuteJournal? _activeJournal;
    private FileStream? _journalOwnership;
    private bool _disposed;

    public WindowsAudioMuteLease(
        AudioSessionSelection selection,
        WindowsAudioSessionEnumerator? sessionEnumerator = null,
        string? recoveryJournalPath = null)
    {
        ArgumentNullException.ThrowIfNull(selection);
        _selection = selection;
        _sessionEnumerator = sessionEnumerator ?? new WindowsAudioSessionEnumerator();
        _recoveryJournal = new AudioMuteRecoveryJournal(recoveryJournalPath);
        _expiryTimer = new System.Threading.Timer(
            static state => ((WindowsAudioMuteLease)state!).OnExpiryTimer(),
            this,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
    }

    public WindowsAudioMuteLease(
        IEnumerable<string>? selectedSessionInstanceIds,
        IEnumerable<uint>? selectedProcessIds = null,
        string? recoveryJournalPath = null)
        : this(
            new AudioSessionSelection(selectedSessionInstanceIds, selectedProcessIds),
            recoveryJournalPath: recoveryJournalPath)
    {
    }

    public AudioMuteLeaseState State => Volatile.Read(ref _state);

    public async ValueTask AcquireOrRenewAsync(
        DateTimeOffset expiresAt,
        MuteReason reason,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (expiresAt <= DateTimeOffset.UtcNow)
        {
            await ReleaseAsync(AudioReleaseReason.LeaseExpired, cancellationToken).ConfigureAwait(false);
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_activeJournal is null)
            {
                await AcquireNewLeaseUnderLockAsync(expiresAt, reason, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await RenewLeaseUnderLockAsync(expiresAt, reason, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask ReleaseAsync(
        AudioReleaseReason reason,
        CancellationToken cancellationToken = default)
    {
        // Once a session may be muted, restoration is a safety action and must
        // not be skipped merely because the caller's operation was cancelled.
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (_activeJournal is null)
            {
                Volatile.Write(ref _state, new AudioMuteLeaseState(false, null));
                _expiryTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                return;
            }

            await ReleaseLeaseUnderLockAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Restores mute states left by an unclean previous process. Call this once at
    /// application startup, before enabling automatic mode. AcquireOrRenewAsync
    /// also performs this repair defensively before starting a new lease.
    /// </summary>
    public static async ValueTask<AudioMuteRecoveryResult> RecoverStaleLeaseAsync(
        WindowsAudioSessionEnumerator? sessionEnumerator = null,
        string? recoveryJournalPath = null,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Core Audio session control is available only on Windows.");
        }

        var journalStore = new AudioMuteRecoveryJournal(recoveryJournalPath);
        if (!journalStore.TryAcquireOwnership(out var ownership))
        {
            return new AudioMuteRecoveryResult(AudioMuteRecoveryStatus.ActiveLease, 0, 0);
        }

        await using (ownership!.ConfigureAwait(false))
        {
            AudioMuteJournal? journal;
            try
            {
                journal = await journalStore.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                return new AudioMuteRecoveryResult(
                    AudioMuteRecoveryStatus.Failed,
                    0,
                    0,
                    exception.Message);
            }

            if (journal is null)
            {
                return new AudioMuteRecoveryResult(AudioMuteRecoveryStatus.NothingToRecover, 0, 0);
            }

            try
            {
                var result = RestoreJournalSessions(sessionEnumerator ?? new(), journal);
                if (result.Unresolved.Count != 0)
                {
                    var unresolvedJournal = CreateUnresolvedJournal(journal, result.Unresolved);
                    await journalStore.WriteAsync(unresolvedJournal, CancellationToken.None).ConfigureAwait(false);
                    return new AudioMuteRecoveryResult(
                        AudioMuteRecoveryStatus.Failed,
                        result.Restored,
                        result.Unresolved.Count,
                        BuildIncompleteRecoveryMessage(result));
                }

                journalStore.Delete();
                return new AudioMuteRecoveryResult(
                    AudioMuteRecoveryStatus.Restored,
                    result.Restored,
                    0);
            }
            catch (Exception exception)
            {
                // Retain the durable journal so a later startup can retry.
                return new AudioMuteRecoveryResult(
                    AudioMuteRecoveryStatus.Failed,
                    0,
                    0,
                    exception.Message);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        Exception? releaseError = null;
        try
        {
            await ReleaseAsync(AudioReleaseReason.Shutdown).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            releaseError = exception;
        }

        _disposed = true;
        _expiryTimer.Dispose();
        _journalOwnership?.Dispose();
        _journalOwnership = null;
        _gate.Dispose();

        if (releaseError is not null)
        {
            throw new InvalidOperationException(
                "The audio mute lease could not fully restore its sessions. The recovery journal was retained.",
                releaseError);
        }
    }

    private async ValueTask AcquireNewLeaseUnderLockAsync(
        DateTimeOffset expiresAt,
        MuteReason reason,
        CancellationToken cancellationToken)
    {
        _journalOwnership = _recoveryJournal.AcquireOwnership();

        try
        {
            var staleJournal = await _recoveryJournal.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (staleJournal is not null)
            {
                var staleResult = RestoreJournalSessions(_sessionEnumerator, staleJournal);
                if (staleResult.Unresolved.Count != 0)
                {
                    await _recoveryJournal.WriteAsync(
                        CreateUnresolvedJournal(staleJournal, staleResult.Unresolved),
                        CancellationToken.None).ConfigureAwait(false);
                    throw new InvalidOperationException(BuildIncompleteRecoveryMessage(staleResult));
                }

                _recoveryJournal.Delete();
            }

            var handles = _sessionEnumerator.EnumerateHandles();
            try
            {
                var selected = handles.Where(handle => _selection.Matches(handle.Info)).ToArray();
                if (selected.Length == 0)
                {
                    throw new AudioSessionNotFoundException();
                }

                var sessions = selected
                    .Select(static handle => CreateJournalSession(handle.Info, handle.GetMute()))
                    .ToArray();

                var journal = new AudioMuteJournal(
                    AudioMuteJournal.CurrentVersion,
                    Guid.NewGuid(),
                    Environment.ProcessId,
                    DateTimeOffset.UtcNow,
                    expiresAt,
                    reason.ToString(),
                    sessions);

                // This durable write must complete before the first mute change.
                await _recoveryJournal.WriteAsync(journal, cancellationToken).ConfigureAwait(false);

                try
                {
                    foreach (var handle in selected)
                    {
                        handle.SetMute(true);
                    }
                }
                catch (Exception muteError)
                {
                    var rollback = RestoreHandles(selected, sessions);
                    if (rollback.Unresolved.Count == 0)
                    {
                        _recoveryJournal.Delete();
                        throw;
                    }

                    // Keep the original durable journal until the narrower rewrite
                    // succeeds, so a write failure can never lose recovery data.
                    MarkRestorePending(journal);
                    var unresolvedJournal = CreateUnresolvedJournal(journal, rollback.Unresolved);
                    await _recoveryJournal.WriteAsync(
                        unresolvedJournal,
                        CancellationToken.None).ConfigureAwait(false);
                    MarkRestorePending(unresolvedJournal);

                    throw new AggregateException(
                        "Muting a selected session failed and one or more original mute states could not be restored immediately.",
                        new[] { muteError }.Concat(rollback.Errors));
                }

                _activeJournal = journal;
                Volatile.Write(ref _state, new AudioMuteLeaseState(true, expiresAt));
                ScheduleExpiry(expiresAt);
            }
            finally
            {
                WindowsAudioSessionEnumerator.DisposeAll(handles);
            }
        }
        catch
        {
            if (_activeJournal is null)
            {
                _journalOwnership?.Dispose();
                _journalOwnership = null;
            }

            throw;
        }
    }

    private async ValueTask RenewLeaseUnderLockAsync(
        DateTimeOffset expiresAt,
        MuteReason reason,
        CancellationToken cancellationToken)
    {
        var currentJournal = _activeJournal
            ?? throw new InvalidOperationException("There is no active audio mute lease to renew.");

        var handles = _sessionEnumerator.EnumerateHandles();
        try
        {
            var assignedHandles = new HashSet<IAudioSessionHandle>(ReferenceEqualityComparer.Instance);
            var matchingHandles = new List<IAudioSessionHandle>();
            var renewedSessions = new List<AudioMuteJournalSession>(currentJournal.Sessions.Count);

            // Follow a previously muted logical session across a Core Audio
            // instance restart by its stable identifier. This does not widen the
            // user's exact-session selection to other sessions in the same PID.
            foreach (var savedSession in currentJournal.Sessions)
            {
                var handle = FindAvailableHandle(savedSession, handles, assignedHandles);
                if (handle is null)
                {
                    renewedSessions.Add(savedSession);
                    continue;
                }

                assignedHandles.Add(handle);
                matchingHandles.Add(handle);
                renewedSessions.Add(UpdateJournalIdentity(savedSession, handle.Info));
            }

            // Process-wide matching remains an explicit, separate selection mode.
            // With any exact IDs present, AudioSessionSelection.Matches never
            // reaches this branch for a merely same-PID session.
            foreach (var handle in handles)
            {
                if (assignedHandles.Contains(handle) || !_selection.Matches(handle.Info))
                {
                    continue;
                }

                assignedHandles.Add(handle);
                matchingHandles.Add(handle);
                renewedSessions.Add(CreateJournalSession(handle.Info, handle.GetMute()));
            }

            var renewedJournal = currentJournal with
            {
                Version = AudioMuteJournal.CurrentVersion,
                ExpiresAt = expiresAt,
                Reason = reason.ToString(),
                Sessions = renewedSessions,
            };

            // Persist original states for newly appeared sessions before muting them.
            await _recoveryJournal.WriteAsync(renewedJournal, cancellationToken).ConfigureAwait(false);

            try
            {
                foreach (var handle in matchingHandles)
                {
                    handle.SetMute(true);
                }
            }
            catch
            {
                // A renewal that cannot maintain a mute lease fails open.
                _activeJournal = renewedJournal;
                await ReleaseLeaseUnderLockAsync().ConfigureAwait(false);
                throw;
            }

            _activeJournal = renewedJournal;
            Volatile.Write(ref _state, new AudioMuteLeaseState(true, expiresAt));
            ScheduleExpiry(expiresAt);
        }
        finally
        {
            WindowsAudioSessionEnumerator.DisposeAll(handles);
        }
    }

    private async ValueTask ReleaseLeaseUnderLockAsync()
    {
        var journal = _activeJournal;
        if (journal is null)
        {
            return;
        }

        AudioSessionRestoreResult result;
        try
        {
            result = RestoreJournalSessions(_sessionEnumerator, journal);
        }
        catch
        {
            // Retain both the in-memory and durable lease, then retry shortly.
            MarkRestorePending(journal);
            throw;
        }

        if (result.Unresolved.Count != 0)
        {
            var unresolvedJournal = CreateUnresolvedJournal(journal, result.Unresolved);
            try
            {
                // Restoration is a safety action. Once a session may be muted,
                // caller cancellation cannot suppress durable recovery state.
                await _recoveryJournal.WriteAsync(
                    unresolvedJournal,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                MarkRestorePending(journal);
                throw;
            }

            MarkRestorePending(unresolvedJournal);
            throw new InvalidOperationException(BuildIncompleteRecoveryMessage(result));
        }

        try
        {
            _recoveryJournal.Delete();
        }
        catch
        {
            // Restoration is already complete. A stale journal is safe and
            // idempotent; the next startup will restore the same exact values.
        }

        _activeJournal = null;
        Volatile.Write(ref _state, new AudioMuteLeaseState(false, null));
        _expiryTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _journalOwnership?.Dispose();
        _journalOwnership = null;
    }

    private void ScheduleExpiry(DateTimeOffset expiresAt)
    {
        var delay = expiresAt - DateTimeOffset.UtcNow;
        if (delay < TimeSpan.Zero)
        {
            delay = TimeSpan.Zero;
        }

        var maximumTimerDelay = TimeSpan.FromMilliseconds(uint.MaxValue - 1);
        if (delay > maximumTimerDelay)
        {
            delay = maximumTimerDelay;
        }

        _expiryTimer.Change(delay, Timeout.InfiniteTimeSpan);
    }

    private void OnExpiryTimer()
    {
        _ = ReleaseIfExpiredAsync();
    }

    private async Task ReleaseIfExpiredAsync()
    {
        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_activeJournal is null || _disposed)
                {
                    return;
                }

                var expiresAt = State.ExpiresAt;
                if (expiresAt is not null && expiresAt > DateTimeOffset.UtcNow)
                {
                    ScheduleExpiry(expiresAt.Value);
                    return;
                }

                await ReleaseLeaseUnderLockAsync().ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch
        {
            // ReleaseLeaseUnderLockAsync retained the journal and scheduled a retry.
        }
    }

    private void MarkRestorePending(AudioMuteJournal journal)
    {
        _activeJournal = journal;
        Volatile.Write(
            ref _state,
            new AudioMuteLeaseState(true, DateTimeOffset.UtcNow + RestoreRetryDelay));
        _expiryTimer.Change(RestoreRetryDelay, Timeout.InfiniteTimeSpan);
    }

    private static AudioSessionRestoreResult RestoreJournalSessions(
        WindowsAudioSessionEnumerator sessionEnumerator,
        AudioMuteJournal journal)
    {
        var handles = sessionEnumerator.EnumerateHandles();
        try
        {
            return RestoreHandles(handles, journal.Sessions);
        }
        finally
        {
            WindowsAudioSessionEnumerator.DisposeAll(handles);
        }
    }

    private static AudioSessionRestoreResult RestoreHandles(
        IReadOnlyList<IAudioSessionHandle> handles,
        IReadOnlyList<AudioMuteJournalSession> savedSessions)
    {
        var assignedHandles = new HashSet<IAudioSessionHandle>(ReferenceEqualityComparer.Instance);
        var unresolved = new List<AudioMuteJournalSession>();
        var errors = new List<Exception>();
        var restored = 0;

        foreach (var savedSession in savedSessions)
        {
            var handle = FindAvailableHandle(savedSession, handles, assignedHandles);
            if (handle is null)
            {
                unresolved.Add(savedSession);
                continue;
            }

            assignedHandles.Add(handle);
            var currentIdentity = UpdateJournalIdentity(savedSession, handle.Info);
            try
            {
                handle.SetMute(savedSession.OriginalMuted);
                restored++;
            }
            catch (Exception exception)
            {
                errors.Add(exception);
                unresolved.Add(currentIdentity);
            }
        }

        return new AudioSessionRestoreResult(restored, unresolved, errors);
    }

    private static IAudioSessionHandle? FindAvailableHandle(
        AudioMuteJournalSession savedSession,
        IReadOnlyList<IAudioSessionHandle> handles,
        ISet<IAudioSessionHandle> assignedHandles)
    {
        bool IsAvailable(IAudioSessionHandle handle) =>
            !assignedHandles.Contains(handle)
            && !handle.Info.IsSystemSoundsSession
            && handle.Info.State != AudioSessionActivityState.Expired
            && string.Equals(
                handle.Info.EndpointId,
                savedSession.EndpointId,
                StringComparison.Ordinal);

        if (!string.IsNullOrWhiteSpace(savedSession.SessionInstanceId))
        {
            var exact = handles
                .Where(handle =>
                    IsAvailable(handle)
                    && string.Equals(
                        handle.Info.SessionInstanceId,
                        savedSession.SessionInstanceId,
                        StringComparison.Ordinal))
                .ToArray();
            if (exact.Length == 1)
            {
                return exact[0];
            }
        }

        if (string.IsNullOrWhiteSpace(savedSession.SessionIdentifier))
        {
            return null;
        }

        var stable = handles
            .Where(handle =>
                IsAvailable(handle)
                && string.Equals(
                    handle.Info.SessionIdentifier,
                    savedSession.SessionIdentifier,
                    StringComparison.Ordinal))
            .ToArray();

        // An ambiguous stable identifier must not broaden recovery to several
        // sessions. Keep the journal entry and retry when identity is unique.
        return stable.Length == 1 ? stable[0] : null;
    }

    private static AudioMuteJournalSession CreateJournalSession(
        AudioSessionInfo info,
        bool originalMuted) =>
        new(
            info.EndpointId,
            info.SessionIdentifier,
            info.SessionInstanceId,
            info.ProcessId,
            info.DisplayName,
            originalMuted);

    private static AudioMuteJournalSession UpdateJournalIdentity(
        AudioMuteJournalSession savedSession,
        AudioSessionInfo info) =>
        savedSession with
        {
            EndpointId = info.EndpointId,
            SessionIdentifier = info.SessionIdentifier,
            SessionInstanceId = info.SessionInstanceId,
            ProcessId = info.ProcessId,
            DisplayName = info.DisplayName,
        };

    private static AudioMuteJournal CreateUnresolvedJournal(
        AudioMuteJournal journal,
        IReadOnlyList<AudioMuteJournalSession> unresolved) =>
        journal with
        {
            Version = AudioMuteJournal.CurrentVersion,
            Sessions = unresolved.ToArray(),
        };

    private static string BuildIncompleteRecoveryMessage(AudioSessionRestoreResult result)
    {
        var cause = result.Errors.Count == 0
            ? "not currently available"
            : "not currently available or could not be changed";
        return $"{result.Unresolved.Count} audio session(s) are {cause}. " +
            "Their original mute states remain in the recovery journal for retry.";
    }

    private sealed record AudioSessionRestoreResult(
        int Restored,
        IReadOnlyList<AudioMuteJournalSession> Unresolved,
        IReadOnlyList<Exception> Errors);
}
