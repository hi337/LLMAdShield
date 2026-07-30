using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LLMAdShield.Windows.Platform.Audio;

internal sealed class AudioMuteRecoveryJournal
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    internal AudioMuteRecoveryJournal(string? path = null)
    {
        Path = path ?? DefaultPath;
        LockPath = Path + ".lock";
    }

    internal static string DefaultPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LLMAdShield",
        "audio-mute-recovery.json");

    internal string Path { get; }

    private string LockPath { get; }

    internal FileStream AcquireOwnership()
    {
        var directory = System.IO.Path.GetDirectoryName(Path)
            ?? throw new InvalidOperationException("The audio recovery-journal path has no parent directory.");
        Directory.CreateDirectory(directory);

        try
        {
            return new FileStream(
                LockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);
        }
        catch (IOException exception)
        {
            throw new AudioMuteLeaseInUseException(exception);
        }
    }

    internal bool TryAcquireOwnership(out FileStream? ownership)
    {
        try
        {
            ownership = AcquireOwnership();
            return true;
        }
        catch (AudioMuteLeaseInUseException)
        {
            ownership = null;
            return false;
        }
    }

    internal async ValueTask<AudioMuteJournal?> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(Path))
        {
            return null;
        }

        await using var stream = new FileStream(
            Path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var journal = await JsonSerializer.DeserializeAsync<AudioMuteJournal>(
            stream,
            SerializerOptions,
            cancellationToken).ConfigureAwait(false);

        if (journal is null
            || journal.Version < AudioMuteJournal.MinimumSupportedVersion
            || journal.Version > AudioMuteJournal.CurrentVersion)
        {
            throw new InvalidDataException("The audio mute recovery journal is empty or has an unsupported version.");
        }

        if (journal.Sessions is null
            || journal.Sessions.Count == 0
            || journal.Sessions.Any(static session =>
                string.IsNullOrWhiteSpace(session.EndpointId)
                || (string.IsNullOrWhiteSpace(session.SessionIdentifier)
                    && string.IsNullOrWhiteSpace(session.SessionInstanceId))))
        {
            throw new InvalidDataException("The audio mute recovery journal contains no valid sessions.");
        }

        return journal;
    }

    internal async ValueTask WriteAsync(
        AudioMuteJournal journal,
        CancellationToken cancellationToken)
    {
        var directory = System.IO.Path.GetDirectoryName(Path)
            ?? throw new InvalidOperationException("The audio recovery-journal path has no parent directory.");
        Directory.CreateDirectory(directory);

        var temporaryPath = Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(journal, SerializerOptions);
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, Path, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch
            {
                // A leftover temporary file does not affect the last durable journal.
            }
        }
    }

    internal void Delete()
    {
        File.Delete(Path);
    }
}

internal sealed record AudioMuteJournal(
    int Version,
    Guid LeaseId,
    int OwnerProcessId,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    string Reason,
    IReadOnlyList<AudioMuteJournalSession> Sessions)
{
    internal const int MinimumSupportedVersion = 1;
    internal const int CurrentVersion = 2;
}

internal sealed record AudioMuteJournalSession(
    string EndpointId,
    string? SessionIdentifier,
    string SessionInstanceId,
    uint ProcessId,
    string DisplayName,
    bool OriginalMuted);

public sealed class AudioMuteLeaseInUseException : InvalidOperationException
{
    internal AudioMuteLeaseInUseException(Exception innerException)
        : base("Another LLM AdShield process currently owns the audio mute lease.", innerException)
    {
    }
}
