using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LLMAdShield.Core;
using LLMAdShield.Windows.Configuration;

namespace LLMAdShield.Windows.Services;

internal sealed class AnthropicAdClassifier : IAdClassifier, IDisposable
{
    private const string Endpoint = "https://api.anthropic.com/v1/messages";
    private const string ApiVersion = "2023-06-01";
    private readonly HttpClient _httpClient;
    private readonly string _model;
    private readonly ModelPricing _pricing;
    private readonly bool _ownsClient;

    internal const int MaximumOutputTokens = 256;

    internal decimal MaximumEstimatedRequestCostUsd =>
        _pricing.MaximumRequestCostUsd(4096, MaximumOutputTokens);

    public AnthropicAdClassifier(
        string apiKey,
        string model,
        ModelPricing pricing,
        HttpClient? httpClient = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        _model = model;
        _pricing = pricing;
        _ownsClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _httpClient.DefaultRequestHeaders.Add("x-api-key", apiKey);
        _httpClient.DefaultRequestHeaders.Add("anthropic-version", ApiVersion);
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("LLMAdShield-Windows/0.2");
    }

    public async ValueTask<ClassificationResult> ClassifyAsync(
        FrameWindow frameWindow,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frameWindow);

        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                Content = new StringContent(
                    BuildRequestJson(frameWindow),
                    Encoding.UTF8,
                    "application/json"),
            };
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            await using var responseStream = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(
                responseStream,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            stopwatch.Stop();
            var completedAt = startedAt + stopwatch.Elapsed;
            var usage = ReadUsage(document.RootElement);
            if (!response.IsSuccessStatusCode)
            {
                return Failure(
                    frameWindow,
                    startedAt,
                    completedAt,
                    ReadErrorCode(document.RootElement) ?? $"http_{(int)response.StatusCode}",
                    usage,
                    chargeMaximumWhenUsageMissing: true);
            }

            var stopReason = ReadString(document.RootElement, "stop_reason");
            if (string.Equals(stopReason, "refusal", StringComparison.Ordinal))
            {
                return new ClassificationResult
                {
                    Outcome = ClassificationOutcome.Refusal,
                    Model = ReadString(document.RootElement, "model") ?? _model,
                    FrameWindowEndedAt = frameWindow.EndedAt,
                    StartedAt = startedAt,
                    CompletedAt = completedAt,
                    Usage = usage,
                    EstimatedCostUsd = CalculateCost(usage),
                    ErrorCode = "refusal",
                };
            }

            if (!string.Equals(stopReason, "end_turn", StringComparison.Ordinal))
            {
                return Failure(
                    frameWindow,
                    startedAt,
                    completedAt,
                    $"message_stopped_{stopReason ?? "unknown"}",
                    usage,
                    chargeMaximumWhenUsageMissing: true);
            }

            var outputText = ReadOutputText(document.RootElement);
            if (string.IsNullOrWhiteSpace(outputText))
            {
                return Failure(frameWindow, startedAt, completedAt, "missing_output", usage);
            }

            AdVerdict verdict;
            try
            {
                verdict = AdVerdictJson.Deserialize(outputText);
            }
            catch (JsonException)
            {
                return Failure(frameWindow, startedAt, completedAt, "invalid_structured_output", usage);
            }

            return new ClassificationResult
            {
                Outcome = ClassificationOutcome.Success,
                Model = ReadString(document.RootElement, "model") ?? _model,
                FrameWindowEndedAt = frameWindow.EndedAt,
                StartedAt = startedAt,
                CompletedAt = completedAt,
                Verdict = verdict,
                Usage = usage,
                EstimatedCostUsd = CalculateCost(usage),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return new ClassificationResult
            {
                Outcome = ClassificationOutcome.Cancelled,
                Model = _model,
                FrameWindowEndedAt = frameWindow.EndedAt,
                StartedAt = startedAt,
                CompletedAt = startedAt + stopwatch.Elapsed,
                EstimatedCostUsd = MaximumEstimatedRequestCostUsd,
                ErrorCode = "cancelled",
            };
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            stopwatch.Stop();
            return Failure(
                frameWindow,
                startedAt,
                startedAt + stopwatch.Elapsed,
                exception is TaskCanceledException ? "timeout" : "request_failed",
                chargeMaximumWhenUsageMissing: true);
        }
    }

    private string BuildRequestJson(FrameWindow frameWindow)
    {
        var content = new JsonArray();
        foreach (var frame in frameWindow.Frames.TakeLast(3))
        {
            content.Add(new JsonObject
            {
                ["type"] = "image",
                ["source"] = new JsonObject
                {
                    ["type"] = "base64",
                    ["media_type"] = NormalizeMediaType(frame.MediaType),
                    ["data"] = Convert.ToBase64String(frame.EncodedImage.Span),
                },
            });
        }

        content.Add(new JsonObject
        {
            ["type"] = "text",
            ["text"] = "The images are ordered oldest to newest. Classify the current segment now.",
        });

        var request = new JsonObject
        {
            ["model"] = _model,
            ["max_tokens"] = MaximumOutputTokens,
            ["system"] = AdClassificationPrompt.Instructions,
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = content,
                },
            },
            ["output_config"] = new JsonObject
            {
                ["format"] = new JsonObject
                {
                    ["type"] = "json_schema",
                    ["schema"] = JsonNode.Parse(AdVerdictJson.Schema),
                },
            },
        };

        return request.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }

    private ClassificationResult Failure(
        FrameWindow frameWindow,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt,
        string errorCode,
        TokenUsage? usage = null,
        bool chargeMaximumWhenUsageMissing = false) =>
        new()
        {
            Outcome = ClassificationOutcome.Error,
            Model = _model,
            FrameWindowEndedAt = frameWindow.EndedAt,
            StartedAt = startedAt,
            CompletedAt = completedAt,
            Usage = usage ?? TokenUsage.Empty,
            EstimatedCostUsd = usage is { TotalTokens: > 0 }
                ? CalculateCost(usage)
                : chargeMaximumWhenUsageMissing ? MaximumEstimatedRequestCostUsd : 0m,
            ErrorCode = errorCode,
        };

    private decimal CalculateCost(TokenUsage usage)
    {
        var cached = Math.Clamp(usage.CachedInputTokens, 0, usage.InputTokens);
        var cacheWrite = Math.Clamp(
            usage.CacheWriteInputTokens,
            0,
            Math.Max(0, usage.InputTokens - cached));
        var uncached = Math.Max(0, usage.InputTokens - cached - cacheWrite);

        return ((uncached * _pricing.InputPerMillionUsd) +
                (cached * _pricing.CachedInputPerMillionUsd) +
                (cacheWrite * _pricing.CacheWriteInputPerMillionUsd) +
                (usage.OutputTokens * _pricing.OutputPerMillionUsd)) / 1_000_000m;
    }

    private static TokenUsage ReadUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return TokenUsage.Empty;
        }

        var uncached = ReadInt(usage, "input_tokens");
        var cacheRead = ReadInt(usage, "cache_read_input_tokens");
        var cacheWrite = ReadInt(usage, "cache_creation_input_tokens");
        var output = ReadInt(usage, "output_tokens");
        return new TokenUsage(uncached + cacheRead + cacheWrite, output, uncached + cacheRead + cacheWrite + output)
        {
            CachedInputTokens = cacheRead,
            CacheWriteInputTokens = cacheWrite,
        };
    }

    private static int ReadInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed) ? parsed : 0;

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? ReadErrorCode(JsonElement root)
    {
        if (!root.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return ReadString(error, "type");
    }

    private static string? ReadOutputText(JsonElement root)
    {
        if (!root.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var part in content.EnumerateArray())
        {
            if (string.Equals(ReadString(part, "type"), "text", StringComparison.Ordinal) &&
                part.TryGetProperty("text", out var text) &&
                text.ValueKind == JsonValueKind.String)
            {
                return text.GetString();
            }
        }

        return null;
    }

    private static string NormalizeMediaType(string mediaType) => mediaType.ToLowerInvariant() switch
    {
        "image/jpeg" => "image/jpeg",
        "image/png" => "image/png",
        "image/gif" => "image/gif",
        "image/webp" => "image/webp",
        _ => throw new ArgumentException("Claude supports JPEG, PNG, GIF, or WebP frame images.", nameof(mediaType)),
    };

    public void Dispose()
    {
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }
}
