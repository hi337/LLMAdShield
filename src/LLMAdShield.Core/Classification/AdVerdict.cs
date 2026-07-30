using System.Text.Json;
using System.Text.Json.Serialization;

namespace LLMAdShield.Core;

[JsonConverter(typeof(JsonStringEnumConverter<ContentLabel>))]
public enum ContentLabel
{
    [JsonStringEnumMemberName("program")]
    Program,

    [JsonStringEnumMemberName("commercial")]
    Commercial,

    [JsonStringEnumMemberName("promo_or_sponsor")]
    PromoOrSponsor,

    [JsonStringEnumMemberName("public_safety")]
    PublicSafety,

    [JsonStringEnumMemberName("unknown")]
    Unknown,
}

[JsonConverter(typeof(JsonStringEnumConverter<ContentTransition>))]
public enum ContentTransition
{
    [JsonStringEnumMemberName("none")]
    None,

    [JsonStringEnumMemberName("ad_start")]
    AdStart,

    [JsonStringEnumMemberName("ad_end")]
    AdEnd,

    [JsonStringEnumMemberName("unclear")]
    Unclear,
}

[JsonConverter(typeof(JsonStringEnumConverter<ConfidenceLevel>))]
public enum ConfidenceLevel
{
    [JsonStringEnumMemberName("low")]
    Low,

    [JsonStringEnumMemberName("medium")]
    Medium,

    [JsonStringEnumMemberName("high")]
    High,
}

[JsonConverter(typeof(JsonStringEnumConverter<EvidenceKind>))]
public enum EvidenceKind
{
    [JsonStringEnumMemberName("brand_or_product")]
    BrandOrProduct,

    [JsonStringEnumMemberName("call_to_action")]
    CallToAction,

    [JsonStringEnumMemberName("price_or_offer")]
    PriceOrOffer,

    [JsonStringEnumMemberName("sponsor_disclosure")]
    SponsorDisclosure,

    [JsonStringEnumMemberName("promo_or_schedule")]
    PromoOrSchedule,

    [JsonStringEnumMemberName("shopping_format")]
    ShoppingFormat,

    [JsonStringEnumMemberName("program_continuity")]
    ProgramContinuity,

    [JsonStringEnumMemberName("emergency_information")]
    EmergencyInformation,

    [JsonStringEnumMemberName("channel_bug_only")]
    ChannelBugOnly,

    [JsonStringEnumMemberName("product_placement")]
    ProductPlacement,

    [JsonStringEnumMemberName("editorial_context")]
    EditorialContext,

    [JsonStringEnumMemberName("fictional_context")]
    FictionalContext,

    [JsonStringEnumMemberName("other")]
    Other,
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AdVerdict
{
    [JsonPropertyName("current_label")]
    public required ContentLabel CurrentLabel { get; init; }

    [JsonPropertyName("transition")]
    public required ContentTransition Transition { get; init; }

    [JsonPropertyName("confidence")]
    public required ConfidenceLevel Confidence { get; init; }

    [JsonPropertyName("evidence")]
    public required IReadOnlyList<EvidenceKind> Evidence { get; init; }

    [JsonPropertyName("needs_more_context")]
    public required bool NeedsMoreContext { get; init; }

    [JsonIgnore]
    public bool IsAdvertisement =>
        CurrentLabel is ContentLabel.Commercial or ContentLabel.PromoOrSponsor;
}

public static class AdVerdictJson
{
    public const string StructuredOutputName = "ad_verdict";

    public const string Schema = """
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "current_label": {
              "type": "string",
              "enum": ["program", "commercial", "promo_or_sponsor", "public_safety", "unknown"]
            },
            "transition": {
              "type": "string",
              "enum": ["none", "ad_start", "ad_end", "unclear"]
            },
            "confidence": {
              "type": "string",
              "enum": ["low", "medium", "high"]
            },
            "evidence": {
              "type": "array",
              "items": {
                "type": "string",
                "enum": [
                  "brand_or_product",
                  "call_to_action",
                  "price_or_offer",
                  "sponsor_disclosure",
                  "promo_or_schedule",
                  "shopping_format",
                  "program_continuity",
                  "emergency_information",
                  "channel_bug_only",
                  "product_placement",
                  "editorial_context",
                  "fictional_context",
                  "other"
                ]
              }
            },
            "needs_more_context": { "type": "boolean" }
          },
          "required": ["current_label", "transition", "confidence", "evidence", "needs_more_context"]
        }
        """;

    public static JsonSerializerOptions SerializerOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static AdVerdict Deserialize(string json) =>
        JsonSerializer.Deserialize<AdVerdict>(json, SerializerOptions)
        ?? throw new JsonException("The classifier returned JSON null instead of an ad verdict.");

    public static string Serialize(AdVerdict verdict) =>
        JsonSerializer.Serialize(verdict, SerializerOptions);
}
