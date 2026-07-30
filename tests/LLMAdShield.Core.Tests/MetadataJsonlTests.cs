using System.Text.Json;
using LLMAdShield.Core;

namespace LLMAdShield.Core.Tests;

public sealed class MetadataJsonlTests
{
    [Fact]
    public void ClassificationRecord_ContainsMetadataButNoFramePayload()
    {
        var record = new ClassificationMetadataLogRecord
        {
            Timestamp = new DateTimeOffset(2026, 7, 28, 12, 0, 1, TimeSpan.Zero),
            FrameWindowEndedAt = new DateTimeOffset(2026, 7, 28, 12, 0, 0, TimeSpan.Zero),
            Model = "gpt-test",
            Outcome = ClassificationOutcome.Success,
            CurrentLabel = ContentLabel.Commercial,
            Transition = ContentTransition.AdStart,
            Confidence = ConfidenceLevel.High,
            Evidence = [EvidenceKind.PriceOrOffer],
            NeedsMoreContext = false,
            LatencyMilliseconds = 240,
            Usage = new TokenUsage(100, 10, 110),
            EstimatedCostUsd = 0.002m,
            WasStale = false,
        };

        var line = MetadataJsonl.SerializeLine(record);
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;

        Assert.Equal("classification", root.GetProperty("event_type").GetString());
        Assert.Equal("commercial", root.GetProperty("current_label").GetString());
        Assert.Equal("price_or_offer", root.GetProperty("evidence")[0].GetString());
        Assert.DoesNotContain("base64", line, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("encoded_image", line, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain('\n', line);
    }

    [Fact]
    public void DecisionRecord_UsesReadableSnakeCaseEnums()
    {
        var record = new DetectionDecisionMetadataLogRecord
        {
            Timestamp = DateTimeOffset.UnixEpoch,
            Action = DetectionAction.ReleaseMute,
            Reason = DetectionReason.PublicSafety,
            IsMuted = false,
        };

        var line = MetadataJsonl.SerializeLine(record);
        using var document = JsonDocument.Parse(line);

        Assert.Equal("release_mute", document.RootElement.GetProperty("action").GetString());
        Assert.Equal("public_safety", document.RootElement.GetProperty("reason").GetString());
    }
}
