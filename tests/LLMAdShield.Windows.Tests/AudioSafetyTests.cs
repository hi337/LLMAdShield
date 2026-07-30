using LLMAdShield.Core;
using LLMAdShield.Windows.Platform.Audio;

namespace LLMAdShield.Windows.Tests;

public sealed class AudioSafetyTests
{
    [Fact]
    public void ExactSessionSelection_DoesNotExpandToSameProcess()
    {
        var selection = new AudioSessionSelection(["selected-instance"], [42]);

        Assert.True(selection.Matches(CreateInfo("stable-a", "selected-instance", processId: 7)));
        Assert.False(selection.Matches(CreateInfo("stable-b", "other-instance", processId: 42)));

        var explicitProcessSelection = new AudioSessionSelection(processIds: [42]);
        Assert.True(explicitProcessSelection.Matches(
            CreateInfo("stable-b", "other-instance", processId: 42)));
    }

    [Fact]
    public async Task StartupRecovery_UsesStableIdentifier_AndRetainsOnlyUnresolvedSessions()
    {
        var testDirectory = CreateTestDirectory();
        var journalPath = Path.Combine(testDirectory, "recovery.json");
        try
        {
            var journal = CreateJournal(
                CreateJournalSession("endpoint", "stable-a", "old-instance-a", originalMuted: false),
                CreateJournalSession("endpoint", "stable-b", "old-instance-b", originalMuted: true));
            var store = new AudioMuteRecoveryJournal(journalPath);
            await store.WriteAsync(journal, CancellationToken.None);

            var available = new FakeAudioSessionHandle(
                CreateInfo("stable-a", "new-instance-a"),
                muted: true);
            var enumerator = new FakeAudioSessionEnumerator(available);

            var first = await WindowsAudioMuteLease.RecoverStaleLeaseAsync(
                enumerator,
                journalPath);

            Assert.Equal(AudioMuteRecoveryStatus.Failed, first.Status);
            Assert.Equal(1, first.RestoredSessionCount);
            Assert.Equal(1, first.MissingSessionCount);
            Assert.False(available.Muted);
            Assert.True(File.Exists(journalPath));

            var unresolved = await store.ReadAsync(CancellationToken.None);
            var pending = Assert.Single(Assert.IsType<AudioMuteJournal>(unresolved).Sessions);
            Assert.Equal(AudioMuteJournal.CurrentVersion, unresolved!.Version);
            Assert.Equal("stable-b", pending.SessionIdentifier);
            Assert.Equal("old-instance-b", pending.SessionInstanceId);
            Assert.True(pending.OriginalMuted);

            var replacement = new FakeAudioSessionHandle(
                CreateInfo("stable-b", "new-instance-b"),
                muted: false);
            enumerator.SetSessions(replacement);

            var second = await WindowsAudioMuteLease.RecoverStaleLeaseAsync(
                enumerator,
                journalPath);

            Assert.Equal(AudioMuteRecoveryStatus.Restored, second.Status);
            Assert.Equal(1, second.RestoredSessionCount);
            Assert.Equal(0, second.MissingSessionCount);
            Assert.True(replacement.Muted);
            Assert.False(File.Exists(journalPath));
        }
        finally
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task InProcessRelease_RetainsMissingSession_WithoutTouchingSameProcessPeer()
    {
        var testDirectory = CreateTestDirectory();
        var journalPath = Path.Combine(testDirectory, "recovery.json");
        var selected = new FakeAudioSessionHandle(
            CreateInfo("stable-selected", "selected-instance", processId: 42),
            muted: false);
        var sameProcessPeer = new FakeAudioSessionHandle(
            CreateInfo("stable-peer", "peer-instance", processId: 42),
            muted: false);
        var replacement = new FakeAudioSessionHandle(
            CreateInfo("stable-selected", "replacement-instance", processId: 42),
            muted: true);
        var enumerator = new FakeAudioSessionEnumerator(selected, sameProcessPeer);
        var lease = new WindowsAudioMuteLease(
            new AudioSessionSelection(["selected-instance"], [42]),
            enumerator,
            journalPath);

        try
        {
            await lease.AcquireOrRenewAsync(
                DateTimeOffset.UtcNow.AddMinutes(1),
                MuteReason.AdvertisementDetected);

            Assert.True(selected.Muted);
            Assert.False(sameProcessPeer.Muted);

            enumerator.SetSessions(sameProcessPeer);
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await lease.ReleaseAsync(AudioReleaseReason.ProgramResumed));

            Assert.False(sameProcessPeer.Muted);
            var store = new AudioMuteRecoveryJournal(journalPath);
            var unresolved = await store.ReadAsync(CancellationToken.None);
            var pending = Assert.Single(Assert.IsType<AudioMuteJournal>(unresolved).Sessions);
            Assert.Equal("stable-selected", pending.SessionIdentifier);
            Assert.False(pending.OriginalMuted);

            enumerator.SetSessions(replacement, sameProcessPeer);
            await lease.ReleaseAsync(AudioReleaseReason.ProgramResumed);

            Assert.False(replacement.Muted);
            Assert.False(sameProcessPeer.Muted);
            Assert.False(File.Exists(journalPath));
        }
        finally
        {
            enumerator.SetSessions(replacement, sameProcessPeer);
            try
            {
                await lease.ReleaseAsync(AudioReleaseReason.Shutdown);
            }
            catch
            {
                // Preserve the original test failure; DisposeAsync keeps any
                // still-unresolved durable recovery entry for a later retry.
            }

            try
            {
                await lease.DisposeAsync();
            }
            catch
            {
                // Preserve the original test failure for assertion reporting.
            }

            Directory.Delete(testDirectory, recursive: true);
        }
    }

    private static AudioSessionInfo CreateInfo(
        string stableIdentifier,
        string instanceIdentifier,
        uint processId = 42) =>
        new(
            "endpoint",
            true,
            stableIdentifier,
            instanceIdentifier,
            processId,
            $"Session {stableIdentifier}",
            AudioSessionActivityState.Active,
            false,
            1f,
            false);

    private static AudioMuteJournalSession CreateJournalSession(
        string endpointId,
        string stableIdentifier,
        string instanceIdentifier,
        bool originalMuted) =>
        new(
            endpointId,
            stableIdentifier,
            instanceIdentifier,
            42,
            $"Session {stableIdentifier}",
            originalMuted);

    private static AudioMuteJournal CreateJournal(params AudioMuteJournalSession[] sessions) =>
        new(
            AudioMuteJournal.CurrentVersion,
            Guid.NewGuid(),
            999,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddMinutes(1),
            MuteReason.AdvertisementDetected.ToString(),
            sessions);

    private static string CreateTestDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "LLMAdShield.Audio.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FakeAudioSessionEnumerator(params FakeAudioSessionHandle[] sessions)
        : WindowsAudioSessionEnumerator
    {
        private IReadOnlyList<FakeAudioSessionHandle> _sessions = sessions;

        internal override List<IAudioSessionHandle> EnumerateHandles() =>
            _sessions.Cast<IAudioSessionHandle>().ToList();

        internal void SetSessions(params FakeAudioSessionHandle[] updated) =>
            _sessions = updated;
    }

    private sealed class FakeAudioSessionHandle(AudioSessionInfo info, bool muted)
        : IAudioSessionHandle
    {
        public AudioSessionInfo Info { get; } = info;

        public bool Muted { get; private set; } = muted;

        public bool GetMute() => Muted;

        public void SetMute(bool value) => Muted = value;

        public void Dispose()
        {
        }
    }
}
