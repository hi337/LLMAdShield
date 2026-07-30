using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using LLMAdShield.Core;
using LLMAdShield.Windows.Configuration;
using LLMAdShield.Windows.Services;

namespace LLMAdShield.Windows.Tests;

public sealed class OpenAiAdClassifierTests
{
    [Fact]
    public async Task ClassifyAsync_SendsPrivacyAndCostControls_AndParsesStrictVerdict()
    {
        var handler = new RecordingHandler(CreateSuccessfulResponse());
        using var client = new HttpClient(handler);
        using var classifier = new OpenAiAdClassifier(
            "test-key",
            AppSettings.PrimaryModel,
            new ModelPricing(1m, 6m),
            client);
        using var window = CreateFrameWindow();

        var result = await classifier.ClassifyAsync(window);

        Assert.Equal(ClassificationOutcome.Success, result.Outcome);
        Assert.Equal(ContentLabel.Commercial, result.Verdict?.CurrentLabel);
        Assert.Equal(123, result.Usage.InputTokens);
        Assert.Equal(25, result.Usage.OutputTokens);
        Assert.Equal(0.000273m, result.EstimatedCostUsd);

        using var request = JsonDocument.Parse(Assert.IsType<string>(handler.RequestBody));
        var root = request.RootElement;
        Assert.Equal("gpt-5.6-luna", root.GetProperty("model").GetString());
        Assert.Equal("default", root.GetProperty("service_tier").GetString());
        Assert.False(root.GetProperty("store").GetBoolean());
        Assert.Equal("none", root.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.Equal("current_turn", root.GetProperty("reasoning").GetProperty("context").GetString());
        Assert.False(root.TryGetProperty("previous_response_id", out _));

        var content = root.GetProperty("input")[0].GetProperty("content");
        var images = content.EnumerateArray()
            .Where(item => item.GetProperty("type").GetString() == "input_image")
            .ToArray();
        Assert.Equal(3, images.Length);
        Assert.All(images, image =>
        {
            Assert.Equal("low", image.GetProperty("detail").GetString());
            Assert.StartsWith("data:image/jpeg;base64,", image.GetProperty("image_url").GetString());
        });

        var format = root.GetProperty("text").GetProperty("format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        Assert.True(format.GetProperty("strict").GetBoolean());
        Assert.False(format.GetProperty("schema").GetProperty("additionalProperties").GetBoolean());
    }

    [Fact]
    public async Task ClassifyAsync_RefusalFailsOpenWithoutVerdict()
    {
        var response = """
            {
              "status": "completed",
              "model": "gpt-5.6-luna",
              "output": [{"type":"message","content":[{"type":"refusal","refusal":"No"}]}],
              "usage": {"input_tokens": 10, "output_tokens": 1, "total_tokens": 11}
            }
            """;
        var handler = new RecordingHandler(response);
        using var client = new HttpClient(handler);
        using var classifier = new OpenAiAdClassifier(
            "test-key",
            AppSettings.PrimaryModel,
            new ModelPricing(1m, 6m),
            client);
        using var window = CreateFrameWindow();

        var result = await classifier.ClassifyAsync(window);

        Assert.Equal(ClassificationOutcome.Refusal, result.Outcome);
        Assert.Null(result.Verdict);
        Assert.Equal("refusal", result.ErrorCode);
    }

    [Fact]
    public async Task ClassifyAsync_IncompleteResponseFailsOpenAndPricesDetailedUsage()
    {
        var response = """
            {
              "status": "incomplete",
              "incomplete_details": {"reason":"max_output_tokens"},
              "model": "gpt-5.6-luna",
              "output": [],
              "usage": {
                "input_tokens": 100,
                "input_tokens_details": {"cached_tokens":20,"cache_write_tokens":30},
                "output_tokens": 10,
                "total_tokens": 110
              }
            }
            """;
        var handler = new RecordingHandler(response);
        using var client = new HttpClient(handler);
        using var classifier = new OpenAiAdClassifier(
            "test-key",
            AppSettings.PrimaryModel,
            new ModelPricing(1m, 6m),
            client);
        using var window = CreateFrameWindow();

        var result = await classifier.ClassifyAsync(window);

        Assert.Equal(ClassificationOutcome.Error, result.Outcome);
        Assert.Null(result.Verdict);
        Assert.Equal("response_incomplete_max_output_tokens", result.ErrorCode);
        Assert.Equal(20, result.Usage.CachedInputTokens);
        Assert.Equal(30, result.Usage.CacheWriteInputTokens);
        Assert.Equal(0.0001495m, result.EstimatedCostUsd);
    }

    [Fact]
    public async Task ClassifyAsync_RequestFailureChargesConservativeMaximum()
    {
        using var client = new HttpClient(new ThrowingHandler());
        using var classifier = new OpenAiAdClassifier(
            "test-key",
            AppSettings.PrimaryModel,
            new ModelPricing(1m, 6m),
            client);
        using var window = CreateFrameWindow();

        var result = await classifier.ClassifyAsync(window);

        Assert.Equal(ClassificationOutcome.Error, result.Outcome);
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
            status = "completed",
            model = "gpt-5.6-luna",
            output = new[]
            {
                new
                {
                    type = "message",
                    content = new[] { new { type = "output_text", text = verdict } },
                },
            },
            usage = new { input_tokens = 123, output_tokens = 25, total_tokens = 148 },
        });
    }

    private sealed class RecordingHandler(string responseBody) : HttpMessageHandler
    {
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new HttpRequestException("simulated");
    }
}
