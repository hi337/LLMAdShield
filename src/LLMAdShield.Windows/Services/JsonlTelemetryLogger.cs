using System.IO;
using System.Text.Json;
using LLMAdShield.Core;

namespace LLMAdShield.Windows.Services;

internal sealed class JsonlTelemetryLogger : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path;

    public JsonlTelemetryLogger(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = System.IO.Path.Combine(directory, $"session-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.jsonl");
    }

    public string Path => _path;

    public async ValueTask WriteAsync(object entry, CancellationToken cancellationToken = default)
    {
        var line = entry is MetadataLogRecord metadata
            ? MetadataJsonl.SerializeLine(metadata) + Environment.NewLine
            : JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await File.AppendAllTextAsync(_path, line, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}
