using System.IO;
using System.Text;
using LLMAdShield.Windows.Configuration;
using LLMAdShield.Windows.Platform.Security;
using LLMAdShield.Windows.Services;

namespace LLMAdShield.Windows.Tests;

public sealed class ProviderConfigurationTests
{
    [Fact]
    public void DpapiStore_RoundTripsWithoutPersistingPlaintext_AndRemoves()
    {
        var directory = CreateTemporaryDirectory();
        const string apiKey = "a-very-secret-test-api-key";
        try
        {
            var store = new WindowsDpapiCredentialStore(directory);

            store.Save(LlmProvider.Anthropic, apiKey);

            Assert.True(store.HasStoredCredential(LlmProvider.Anthropic));
            Assert.Equal(apiKey, store.Read(LlmProvider.Anthropic));
            var ciphertext = File.ReadAllBytes(Path.Combine(directory, "anthropic.dpapi"));
            Assert.DoesNotContain(apiKey, Encoding.UTF8.GetString(ciphertext), StringComparison.Ordinal);

            store.Remove(LlmProvider.Anthropic);
            Assert.False(store.HasStoredCredential(LlmProvider.Anthropic));
            Assert.Null(store.Read(LlmProvider.Anthropic));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CredentialResolver_PrefersEncryptedStore_ThenUsesProviderEnvironmentFallback()
    {
        var store = new InMemoryCredentialStore();
        store.Save(LlmProvider.OpenAi, "stored-openai");
        var environment = new Dictionary<string, string?>
        {
            ["OPENAI_API_KEY"] = "environment-openai",
            ["ANTHROPIC_API_KEY"] = "environment-anthropic",
        };
        var resolver = new ApiCredentialResolver(
            store,
            name => environment.GetValueOrDefault(name));

        var openAi = resolver.Resolve(LlmProvider.OpenAi);
        var anthropic = resolver.Resolve(LlmProvider.Anthropic);

        Assert.Equal("stored-openai", openAi.ApiKey);
        Assert.Equal(ApiCredentialSource.EncryptedStore, openAi.Source);
        Assert.Equal("environment-anthropic", anthropic.ApiKey);
        Assert.Equal(ApiCredentialSource.Environment, anthropic.Source);
    }

    [Fact]
    public void PreferenceStore_RoundTripsProvider_AndInvalidContentFailsToOpenAi()
    {
        var directory = CreateTemporaryDirectory();
        var path = Path.Combine(directory, "settings.json");
        try
        {
            var store = new ProviderPreferenceStore(path);
            store.Save(LlmProvider.Anthropic);
            Assert.Equal(LlmProvider.Anthropic, store.Load());

            File.WriteAllText(path, "not-json");
            Assert.Equal(LlmProvider.OpenAi, store.Load());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ClassifierFactory_UsesPinnedCostConsciousClaudeVisionModel()
    {
        var settings = AppSettings.Default;
        var classifiers = AdClassifierFactory.Create(LlmProvider.Anthropic, "test-key", settings);
        try
        {
            Assert.IsType<AnthropicAdClassifier>(classifiers.Primary);
            Assert.IsType<AnthropicAdClassifier>(classifiers.Escalation);
            Assert.Equal("claude-haiku-4-5-20251001", classifiers.PrimaryModel);
            Assert.Equal(classifiers.PrimaryModel, classifiers.EscalationModel);
            Assert.True(classifiers.CostProfile.PrimaryMaximumRequestCostUsd > 0m);
        }
        finally
        {
            (classifiers.Primary as IDisposable)?.Dispose();
            (classifiers.Escalation as IDisposable)?.Dispose();
        }
    }

    [Fact]
    public void ClassifierFactory_PreservesExplicitOpenAiResponsesModels()
    {
        var settings = AppSettings.Default;
        var classifiers = AdClassifierFactory.Create(LlmProvider.OpenAi, "test-key", settings);
        try
        {
            Assert.IsType<OpenAiAdClassifier>(classifiers.Primary);
            Assert.IsType<OpenAiAdClassifier>(classifiers.Escalation);
            Assert.Equal("gpt-5.6-luna", classifiers.PrimaryModel);
            Assert.Equal("gpt-5.6-terra", classifiers.EscalationModel);
        }
        finally
        {
            (classifiers.Primary as IDisposable)?.Dispose();
            (classifiers.Escalation as IDisposable)?.Dispose();
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "LLMAdShield.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class InMemoryCredentialStore : IApiCredentialStore
    {
        private readonly Dictionary<LlmProvider, string> _values = [];

        public bool HasStoredCredential(LlmProvider provider) => _values.ContainsKey(provider);

        public string? Read(LlmProvider provider) => _values.GetValueOrDefault(provider);

        public void Save(LlmProvider provider, string apiKey) => _values[provider] = apiKey;

        public void Remove(LlmProvider provider) => _values.Remove(provider);
    }
}
