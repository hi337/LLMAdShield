using System.IO;
using System.Text;
using System.Text.Json;

namespace LLMAdShield.Windows.Configuration;

internal sealed class ProviderPreferenceStore
{
    private readonly string _path;

    public ProviderPreferenceStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LLMAdShield",
            "settings.json");
    }

    public LlmProvider Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return LlmProvider.OpenAi;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(_path, Encoding.UTF8));
            if (!document.RootElement.TryGetProperty("provider", out var providerElement) ||
                providerElement.ValueKind != JsonValueKind.String ||
                !Enum.TryParse<LlmProvider>(providerElement.GetString(), ignoreCase: true, out var provider) ||
                !Enum.IsDefined(provider))
            {
                return LlmProvider.OpenAi;
            }

            return provider;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return LlmProvider.OpenAi;
        }
    }

    public void Save(LlmProvider provider)
    {
        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException("The provider settings path has no parent directory.");
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(directory, $"settings-{Guid.NewGuid():N}.tmp");
        try
        {
            var json = JsonSerializer.Serialize(new
            {
                provider = provider.ToString().ToLowerInvariant(),
            });
            File.WriteAllText(temporaryPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
