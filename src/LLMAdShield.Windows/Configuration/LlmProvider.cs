namespace LLMAdShield.Windows.Configuration;

internal enum LlmProvider
{
    OpenAi,
    Anthropic,
}

internal sealed record LlmProviderOption(
    LlmProvider Provider,
    string DisplayName,
    string EnvironmentVariableName);

internal static class LlmProviders
{
    public static IReadOnlyList<LlmProviderOption> Options { get; } =
    [
        new(LlmProvider.OpenAi, "OpenAI", "OPENAI_API_KEY"),
        new(LlmProvider.Anthropic, "Anthropic (Claude)", "ANTHROPIC_API_KEY"),
    ];

    public static LlmProviderOption Get(LlmProvider provider) =>
        Options.Single(option => option.Provider == provider);
}
