using System.Text.Json;
using System.Text.Json.Serialization;

namespace LLMAdShield.Core;

[JsonConverter(typeof(JsonStringEnumConverter<MetadataEventType>))]
public enum MetadataEventType
{
    [JsonStringEnumMemberName("classification")]
    Classification,

    [JsonStringEnumMemberName("detection_decision")]
    DetectionDecision,

    [JsonStringEnumMemberName("manual_annotation")]
    ManualAnnotation,

    [JsonStringEnumMemberName("budget")]
    Budget,

    [JsonStringEnumMemberName("error")]
    Error,
}

[JsonConverter(typeof(JsonStringEnumConverter<ManualAnnotationLabel>))]
public enum ManualAnnotationLabel
{
    [JsonStringEnumMemberName("advertisement")]
    Advertisement,

    [JsonStringEnumMemberName("program")]
    Program,
}

public abstract record MetadataLogRecord
{
    public abstract MetadataEventType EventType { get; }

    public required DateTimeOffset Timestamp { get; init; }
}

public sealed record ClassificationMetadataLogRecord : MetadataLogRecord
{
    public override MetadataEventType EventType => MetadataEventType.Classification;

    public required DateTimeOffset FrameWindowEndedAt { get; init; }

    public required string Model { get; init; }

    public required ClassificationOutcome Outcome { get; init; }

    public ContentLabel? CurrentLabel { get; init; }

    public ContentTransition? Transition { get; init; }

    public ConfidenceLevel? Confidence { get; init; }

    public IReadOnlyList<EvidenceKind> Evidence { get; init; } = [];

    public bool? NeedsMoreContext { get; init; }

    public required double LatencyMilliseconds { get; init; }

    public TokenUsage Usage { get; init; } = TokenUsage.Empty;

    public decimal EstimatedCostUsd { get; init; }

    public bool WasStale { get; init; }

    public string? ErrorCode { get; init; }
}

public sealed record DetectionDecisionMetadataLogRecord : MetadataLogRecord
{
    public override MetadataEventType EventType => MetadataEventType.DetectionDecision;

    public required DetectionAction Action { get; init; }

    public required DetectionReason Reason { get; init; }

    public required bool IsMuted { get; init; }

    public DateTimeOffset? MuteLeaseExpiresAt { get; init; }
}

public sealed record ManualAnnotationMetadataLogRecord : MetadataLogRecord
{
    public override MetadataEventType EventType => MetadataEventType.ManualAnnotation;

    public required ManualAnnotationLabel Label { get; init; }
}

public sealed record BudgetMetadataLogRecord : MetadataLogRecord
{
    public override MetadataEventType EventType => MetadataEventType.Budget;

    public required decimal SpentUsd { get; init; }

    public required BudgetLevel Level { get; init; }

    public required bool CanLaunchRequest { get; init; }
}

public sealed record ErrorMetadataLogRecord : MetadataLogRecord
{
    public override MetadataEventType EventType => MetadataEventType.Error;

    public required string Component { get; init; }

    public required string ErrorCode { get; init; }
}

public static class MetadataJsonl
{
    public static JsonSerializerOptions SerializerOptions { get; } = CreateSerializerOptions();

    public static string SerializeLine(MetadataLogRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return JsonSerializer.Serialize(record, record.GetType(), SerializerOptions);
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            WriteIndented = false,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        return options;
    }
}
