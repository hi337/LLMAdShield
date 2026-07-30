using System.Text.Json;
using LLMAdShield.Core;

namespace LLMAdShield.Core.Tests;

public sealed class AdVerdictJsonTests
{
    [Fact]
    public void Serialize_UsesTheStrictWireContract()
    {
        var verdict = new AdVerdict
        {
            CurrentLabel = ContentLabel.PromoOrSponsor,
            Transition = ContentTransition.AdStart,
            Confidence = ConfidenceLevel.High,
            Evidence = [EvidenceKind.SponsorDisclosure, EvidenceKind.CallToAction],
            NeedsMoreContext = false,
        };

        var json = AdVerdictJson.Serialize(verdict);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal("promo_or_sponsor", root.GetProperty("current_label").GetString());
        Assert.Equal("ad_start", root.GetProperty("transition").GetString());
        Assert.Equal("high", root.GetProperty("confidence").GetString());
        Assert.Equal("sponsor_disclosure", root.GetProperty("evidence")[0].GetString());
        Assert.False(root.GetProperty("needs_more_context").GetBoolean());
    }

    [Fact]
    public void Deserialize_RejectsUnknownProperties()
    {
        const string json = """
            {
              "current_label": "program",
              "transition": "none",
              "confidence": "medium",
              "evidence": ["program_continuity"],
              "needs_more_context": false,
              "image_payload": "not-allowed"
            }
            """;

        Assert.Throws<JsonException>(() => AdVerdictJson.Deserialize(json));
    }

    [Fact]
    public void Schema_IsStrictAndRequiresEveryVerdictField()
    {
        using var document = JsonDocument.Parse(AdVerdictJson.Schema);
        var root = document.RootElement;

        Assert.False(root.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(5, root.GetProperty("required").GetArrayLength());
        Assert.Equal("array", root.GetProperty("properties").GetProperty("evidence").GetProperty("type").GetString());
    }
}
