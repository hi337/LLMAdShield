using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using LLMAdShield.Core;
using LLMAdShield.Windows.Configuration;
using LLMAdShield.Windows.Services;

namespace LLMAdShield.Windows.Tests;

public sealed class AnthropicAdClassifierTests
{
    [Fact]
    public async Task ClassifyAsync_SendsThreeImagesAndStructuredSchema_AndParsesVerdict()
    {
        var handler = new RecordingHandler(CreateSuccessfulResponse());
        using var client = new HttpClient(handler);
        using var classifier = new AnthropicAdClassifier(
            "anthropic-test-key",
            AppSettings.AnthropicDefaultModel,
            new ModelPricing(1m, 5m),
            client);
        using var window = CreateFrameWindow();

        var result = await classifier.ClassifyAsync(window);

        Assert.Equal(ClassificationOutcome.Success, result.Outcome);
        Assert.Equal(ContentLabel.Commercial, result.Verdict?.CurrentLabel);
        Assert.Equal(150, result.Usage.InputTokens);
        Assert.Equal(20, result.Usage.CachedInputTokens);
        Assert.Equal(30, result.Usage.CacheWriteInputTokens);
        Assert.Equal(0.0001895m, result.EstimatedCostUsd);
        Assert.Equal("anthropic-test-key", handler.ApiKey);
        Assert.Equal("2023-06-01", handler.ApiVersion);

        var requestBody = Assert.IsType<string>(handler.RequestBody);
        Assert.DoesNotContain("anthropic-test-key", requestBody, StringComparison.Ordinal);
        using var request = JsonDocument.Parse(requestBody);
        var root = request.RootElement;
        Assert.Equal("claude-haiku-4-5-20251001", root.GetProperty("model").GetString());
        Assert.Equal(256, root.GetProperty("max_tokens").GetInt32());
        Assert.Contains("oldest to newest", root.GetProperty("system").GetString(), StringComparison.Ordinal);

        var content = root.GetProperty("messages")[0].GetProperty("content");
        var images = content.EnumerateArray()
            .Where(item => item.GetProperty("type").GetString() == "image")
            .ToArray();
        Assert.Equal(3, images.Length);
        Assert.All(images, image =>
        {
            var source = image.GetProperty("source");
            Assert.Equal("base64", source.GetProperty("type").GetString());
            Assert.Equal("image/jpeg", source.GetProperty("media_type").GetString());
            Assert.NotEmpty(source.GetProperty("data").GetString()!);
        });

        Assert.Equal("text", content[3].GetProperty("type").GetString());
        var format = root.GetProperty("output_config").GetProperty("format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        Assert.False(format.GetProperty("schema").GetProperty("additionalProperties").GetBoolean());
    }

    [Fact]
    public async Task ClassifyAsync_RefusalFailsOpenWithoutVerdict()
    {
        var response = """
            {
              "model": "claude-haiku-4-5-20251001",
              "stop_reason": "refusal",
              "content": [{"type":"text","text":"I cannot help with that."}],
              "usage": {"input_tokens":10,"output_tokens":4}
            }
            """;
        using var client = new HttpClient(new RecordingHandler(response));
        using var classifier = new AnthropicAdClassifier(
            "test-key",
            AppSettings.AnthropicDefaultModel,
            new ModelPricing(1m, 5m),
            client);
        using var window = CreateFrameWindow();

        var result = await classifier.ClassifyAsync(window);

        Assert.Equal(ClassificationOutcome.Refusal, result.Outcome);
        Assert.Null(result.Verdict);
        Assert.Equal("refusal", result.ErrorCode);
    }

    [Fact]
    public async Task ClassifyAsync_InvalidStructuredOutputFailsOpen()
    {
        var response = """
            {
              "model": "claude-haiku-4-5-20251001",
              "stop_reason": "end_turn",
              "content": [{"type":"text","text":"{\"current_label\":\"commercial\"}"}],
              "usage": {"input_tokens":10,"output_tokens":4}
            }
            """;
        using var client = new HttpClient(new RecordingHandler(response));
        using var classifier = new AnthropicAdClassifier(
            "test-key",
            AppSettings.AnthropicDefaultModel,
            new ModelPricing(1m, 5m),
            client);
        using var window = CreateFrameWindow();

        var result = await classifier.ClassifyAsync(window);

        Assert.Equal(ClassificationOutcome.Error, result.Outcome);
        Assert.Null(result.Verdict);
        Assert.Equal("invalid_structured_output", result.ErrorCode);
    }

    [Fact]
    public async Task ClassifyAsync_HttpFailureChargesConservativeMaximum()
    {
        var response = """
            {"type":"error","error":{"type":"rate_limit_error","message":"rate limited"}}
            """;
        using var client = new HttpClient(new RecordingHandler(response, HttpStatusCode.TooManyRequests));
        using var classifier = new AnthropicAdClassifier(
            "test-key",
            AppSettings.AnthropicDefaultModel,
            new ModelPricing(1m, 5m),
            client);
        using var window = CreateFrameWindow();

        var result = await classifier.ClassifyAsync(window);

        Assert.Equal(ClassificationOutcome.Error, result.Outcome);
        Assert.Equal("rate_limit_error", result.ErrorCode);
        Assert.Equal(classifier.MaximumEstimatedRequestCostUsd, result.EstimatedCostUsd);
    }

    private static FrameWindow CreateFrameWindow()
    {
        using var buffer = FrameRingBuffer.CreateTenSecondBuffer();
        var start = DateTimeOffset.UtcNow.AddSeconds(-2);
        for (var index = 0; index < 3; index++)
        {
            buffer.Add(new CapturedFrame(
                start.AddSeconds(index),
                2,
                2,
                "image/jpeg",
                new byte[] { 0xFF, 0xD8, (byte)index, 0xFF, 0xD9 }));
        }

        Assert.True(buffer.TryCreateLatestWindow(3, out var window));
        return window;
    }

    private static string CreateSuccessfulResponse()
    {
        var verdict = """
            {"current_label":"commercial","transition":"ad_start","confidence":"high","evidence":["call_to_action"],"needs_more_context":false}
            """;
        return JsonSerializer.Serialize(new
        {
            model = AppSettings.AnthropicDefaultModel,
            stop_reason = "end_turn",
            content = new[] { new { type = "text", text = verdict } },
            usage = new
            {
                input_tokens = 100,
                cache_read_input_tokens = 20,
                cache_creation_input_tokens = 30,
                output_tokens = 10,
            },
        });
    }

    private sealed class RecordingHandler(
        string responseBody,
        HttpStatusCode statusCode = HttpStatusCode.OK) : HttpMessageHandler
    {
        public string? RequestBody { get; private set; }

        public string? ApiKey { get; private set; }

        public string? ApiVersion { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            ApiKey = request.Headers.GetValues("x-api-key").Single();
            ApiVersion = request.Headers.GetValues("anthropic-version").Single();
            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            };
        }
    }
}
