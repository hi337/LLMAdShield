using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LLMAdShield.Core;
using LLMAdShield.Windows.Configuration;

namespace LLMAdShield.Windows.Services;

internal sealed class OpenAiAdClassifier : IAdClassifier, IDisposable
{
    private const string Endpoint = "https://api.openai.com/v1/responses";

    private readonly HttpClient _httpClient;
    private readonly string _model;
    private readonly ModelPricing _pricing;
    private readonly bool _ownsClient;

    internal const int MaximumOutputTokens = 256;

    internal decimal MaximumEstimatedRequestCostUsd =>
        _pricing.MaximumRequestCostUsd(4096, MaximumOutputTokens);

    public OpenAiAdClassifier(
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
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("LLMAdShield-Windows/0.1");
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

            var status = ReadString(document.RootElement, "status");
            if (!string.Equals(status, "completed", StringComparison.Ordinal))
            {
                return Failure(
                    frameWindow,
                    startedAt,
                    completedAt,
                    ReadIncompleteReason(document.RootElement, status),
                    usage,
                    chargeMaximumWhenUsageMissing: true);
            }

            var outputText = ReadOutputText(document.RootElement, out var refused);
            if (refused)
            {
                return new ClassificationResult
                {
                    Outcome = ClassificationOutcome.Refusal,
                    Model = ReadModel(document.RootElement) ?? _model,
                    FrameWindowEndedAt = frameWindow.EndedAt,
                    StartedAt = startedAt,
                    CompletedAt = completedAt,
                    Usage = usage,
                    EstimatedCostUsd = CalculateCost(usage),
                    ErrorCode = "refusal",
                };
            }

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
                Model = ReadModel(document.RootElement) ?? _model,
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
        var content = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "input_text",
                ["text"] = AdClassificationPrompt.Instructions,
            },
        };

        foreach (var frame in frameWindow.Frames.TakeLast(3))
        {
            content.Add(new JsonObject
            {
                ["type"] = "input_image",
                ["image_url"] = $"data:{frame.MediaType};base64,{Convert.ToBase64String(frame.EncodedImage.Span)}",
                ["detail"] = "low",
            });
        }

        var request = new JsonObject
        {
            ["model"] = _model,
            ["service_tier"] = "default",
            ["store"] = false,
            ["max_output_tokens"] = MaximumOutputTokens,
            ["reasoning"] = new JsonObject
            {
                ["effort"] = "none",
                ["context"] = "current_turn",
            },
            ["input"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = content,
                },
            },
            ["text"] = new JsonObject
            {
                ["format"] = new JsonObject
                {
                    ["type"] = "json_schema",
                    ["name"] = AdVerdictJson.StructuredOutputName,
                    ["strict"] = true,
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

    private static string? ReadModel(JsonElement root) =>
        root.TryGetProperty("model", out var model) ? model.GetString() : null;

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string ReadIncompleteReason(JsonElement root, string? status)
    {
        if (root.TryGetProperty("incomplete_details", out var details) &&
            details.ValueKind == JsonValueKind.Object &&
            details.TryGetProperty("reason", out var reason) &&
            reason.ValueKind == JsonValueKind.String)
        {
            return $"response_{status ?? "unknown"}_{reason.GetString()}";
        }

        return $"response_{status ?? "unknown"}";
    }

    private static string? ReadErrorCode(JsonElement root)
    {
        if (!root.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return error.TryGetProperty("code", out var code)
            ? code.GetString()
            : error.TryGetProperty("type", out var type) ? type.GetString() : null;
    }

    private static TokenUsage ReadUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return TokenUsage.Empty;
        }

        var result = new TokenUsage(
            ReadInt(usage, "input_tokens"),
            ReadInt(usage, "output_tokens"),
            ReadInt(usage, "total_tokens"));

        if (usage.TryGetProperty("input_tokens_details", out var details) &&
            details.ValueKind == JsonValueKind.Object)
        {
            result = result with
            {
                CachedInputTokens = ReadInt(details, "cached_tokens"),
                CacheWriteInputTokens = ReadInt(details, "cache_write_tokens"),
            };
        }

        return result;
    }

    private static int ReadInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed) ? parsed : 0;

    private static string? ReadOutputText(JsonElement root, out bool refused)
    {
        refused = false;
        if (!root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in output.EnumerateArray())
        {
            if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var part in content.EnumerateArray())
            {
                var type = part.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
                if (string.Equals(type, "refusal", StringComparison.Ordinal))
                {
                    refused = true;
                    return null;
                }

                if (string.Equals(type, "output_text", StringComparison.Ordinal) &&
                    part.TryGetProperty("text", out var text))
                {
                    return text.GetString();
                }
            }
        }

        return null;
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }
}
