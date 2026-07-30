using System.IO;

namespace LLMAdShield.Windows.Configuration;

internal sealed record ModelPricing(decimal InputPerMillionUsd, decimal OutputPerMillionUsd)
{
    public decimal CachedInputPerMillionUsd => InputPerMillionUsd * 0.10m;

    public decimal CacheWriteInputPerMillionUsd => InputPerMillionUsd * 1.25m;

    public decimal MaximumRequestCostUsd(int maximumInputTokens, int maximumOutputTokens) =>
        ((maximumInputTokens * CacheWriteInputPerMillionUsd) +
         (maximumOutputTokens * OutputPerMillionUsd)) / 1_000_000m;
}

internal sealed record AppSettings
{
    public const string PrimaryModel = "gpt-5.6-luna";
    public const string EscalationModel = "gpt-5.6-terra";
    public const string AnthropicDefaultModel = "claude-haiku-4-5-20251001";

    public static AppSettings Default { get; } = new();

    public TimeSpan NormalClassificationInterval { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan ActiveClassificationInterval { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaximumResultAge { get; init; } = TimeSpan.FromSeconds(3);

    public TimeSpan FrameRetention { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan CaptureSilenceTimeout { get; init; } = TimeSpan.FromSeconds(3);

    public int MaximumEstimatedInputTokensPerRequest { get; init; } = 4096;

    public int MaximumOutputTokensPerRequest { get; init; } = 256;

    public string OpenAiPrimaryModel { get; init; } = PrimaryModel;

    public string OpenAiEscalationModel { get; init; } = EscalationModel;

    public string AnthropicPrimaryModel { get; init; } = AnthropicDefaultModel;

    public string AnthropicEscalationModel { get; init; } = AnthropicDefaultModel;

    public decimal WarningBudgetUsd { get; init; } = 8m;

    public decimal MaximumBudgetUsd { get; init; } = 10m;

    public IReadOnlyDictionary<string, ModelPricing> Pricing { get; init; } =
        new Dictionary<string, ModelPricing>(StringComparer.Ordinal)
        {
            [PrimaryModel] = new(1.00m, 6.00m),
            [EscalationModel] = new(2.50m, 15.00m),
            [AnthropicDefaultModel] = new(1.00m, 5.00m),
        };

    public string TelemetryDirectory { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LLMAdShield",
        "logs");
}
