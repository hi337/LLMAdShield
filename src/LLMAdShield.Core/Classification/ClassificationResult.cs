using System.Text.Json.Serialization;

namespace LLMAdShield.Core;

[JsonConverter(typeof(JsonStringEnumConverter<ClassificationOutcome>))]
public enum ClassificationOutcome
{
    [JsonStringEnumMemberName("success")]
    Success,

    [JsonStringEnumMemberName("refusal")]
    Refusal,

    [JsonStringEnumMemberName("error")]
    Error,

    [JsonStringEnumMemberName("cancelled")]
    Cancelled,
}

public sealed record TokenUsage(
    [property: JsonPropertyName("input_tokens")] int InputTokens,
    [property: JsonPropertyName("output_tokens")] int OutputTokens,
    [property: JsonPropertyName("total_tokens")] int TotalTokens)
{
    [JsonPropertyName("cached_input_tokens")]
    public int CachedInputTokens { get; init; }

    [JsonPropertyName("cache_write_input_tokens")]
    public int CacheWriteInputTokens { get; init; }

    public static TokenUsage Empty { get; } = new(0, 0, 0);
}

public sealed record ClassificationResult
{
    public required ClassificationOutcome Outcome { get; init; }

    public required string Model { get; init; }

    public required DateTimeOffset FrameWindowEndedAt { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public required DateTimeOffset CompletedAt { get; init; }

    public AdVerdict? Verdict { get; init; }

    public TokenUsage Usage { get; init; } = TokenUsage.Empty;

    public decimal EstimatedCostUsd { get; init; }

    public string? ErrorCode { get; init; }

    [JsonIgnore]
    public TimeSpan Latency => CompletedAt >= StartedAt
        ? CompletedAt - StartedAt
        : TimeSpan.Zero;

    [JsonIgnore]
    public bool HasUsableVerdict => Outcome == ClassificationOutcome.Success && Verdict is not null;
}
