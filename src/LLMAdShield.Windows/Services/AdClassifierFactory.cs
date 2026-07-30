using LLMAdShield.Core;
using LLMAdShield.Windows.Configuration;

namespace LLMAdShield.Windows.Services;

internal sealed record ClassifierCostProfile(
    decimal PrimaryMaximumRequestCostUsd,
    decimal EscalationMaximumRequestCostUsd);

internal sealed record AdClassifierSet(
    IAdClassifier Primary,
    IAdClassifier Escalation,
    string PrimaryModel,
    string EscalationModel,
    ClassifierCostProfile CostProfile);

internal static class AdClassifierFactory
{
    public static AdClassifierSet Create(
        LlmProvider provider,
        string apiKey,
        AppSettings settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentNullException.ThrowIfNull(settings);

        var (primaryModel, escalationModel) = GetModels(provider, settings);
        var primaryPricing = GetPricing(settings, primaryModel);
        var escalationPricing = GetPricing(settings, escalationModel);

        IAdClassifier primary;
        IAdClassifier escalation;
        switch (provider)
        {
            case LlmProvider.OpenAi:
                primary = new OpenAiAdClassifier(apiKey, primaryModel, primaryPricing);
                escalation = new OpenAiAdClassifier(apiKey, escalationModel, escalationPricing);
                break;
            case LlmProvider.Anthropic:
                primary = new AnthropicAdClassifier(apiKey, primaryModel, primaryPricing);
                escalation = new AnthropicAdClassifier(apiKey, escalationModel, escalationPricing);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(provider));
        }

        return new(
            primary,
            escalation,
            primaryModel,
            escalationModel,
            new(
                MaximumCost(settings, primaryPricing),
                MaximumCost(settings, escalationPricing)));
    }

    public static (string Primary, string Escalation) GetModels(
        LlmProvider provider,
        AppSettings settings) => provider switch
        {
            LlmProvider.OpenAi => (settings.OpenAiPrimaryModel, settings.OpenAiEscalationModel),
            LlmProvider.Anthropic => (settings.AnthropicPrimaryModel, settings.AnthropicEscalationModel),
            _ => throw new ArgumentOutOfRangeException(nameof(provider)),
        };

    private static ModelPricing GetPricing(AppSettings settings, string model) =>
        settings.Pricing.TryGetValue(model, out var pricing)
            ? pricing
            : throw new InvalidOperationException($"No pricing configuration exists for model '{model}'.");

    private static decimal MaximumCost(AppSettings settings, ModelPricing pricing) =>
        pricing.MaximumRequestCostUsd(
            settings.MaximumEstimatedInputTokensPerRequest,
            settings.MaximumOutputTokensPerRequest);
}
