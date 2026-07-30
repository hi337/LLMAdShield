using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using LLMAdShield.Windows.Configuration;

namespace LLMAdShield.Windows.Platform.Security;

internal interface IApiCredentialStore
{
    bool HasStoredCredential(LlmProvider provider);

    string? Read(LlmProvider provider);

    void Save(LlmProvider provider, string apiKey);

    void Remove(LlmProvider provider);
}

internal sealed class WindowsDpapiCredentialStore : IApiCredentialStore
{
    private const int CryptProtectUiForbidden = 0x1;
    private static readonly byte[] OptionalEntropy = Encoding.UTF8.GetBytes("LLMAdShield.ProviderCredential.v1");
    private readonly string _directory;

    public WindowsDpapiCredentialStore(string? directory = null)
    {
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LLMAdShield",
            "secrets");
    }

    public bool HasStoredCredential(LlmProvider provider) => File.Exists(GetPath(provider));

    public string? Read(LlmProvider provider)
    {
        var path = GetPath(provider);
        if (!File.Exists(path))
        {
            return null;
        }

        var encrypted = File.ReadAllBytes(path);
        byte[]? plaintext = null;
        try
        {
            plaintext = Unprotect(encrypted);
            var value = Encoding.UTF8.GetString(plaintext);
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encrypted);
            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    public void Save(LlmProvider provider, string apiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        var plaintext = Encoding.UTF8.GetBytes(apiKey.Trim());
        byte[]? encrypted = null;
        try
        {
            encrypted = Protect(plaintext);
            Directory.CreateDirectory(_directory);
            var destination = GetPath(provider);
            var temporaryPath = Path.Combine(_directory, $"credential-{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllBytes(temporaryPath, encrypted);
                File.Move(temporaryPath, destination, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            if (encrypted is not null)
            {
                CryptographicOperations.ZeroMemory(encrypted);
            }
        }
    }

    public void Remove(LlmProvider provider)
    {
        var path = GetPath(provider);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private string GetPath(LlmProvider provider) => Path.Combine(
        _directory,
        provider switch
        {
            LlmProvider.OpenAi => "openai.dpapi",
            LlmProvider.Anthropic => "anthropic.dpapi",
            _ => throw new ArgumentOutOfRangeException(nameof(provider)),
        });

    private static byte[] Protect(byte[] plaintext) => Transform(
        plaintext,
        static (ref DataBlob input, ref DataBlob entropy, out DataBlob output) =>
            CryptProtectData(
                ref input,
                null,
                ref entropy,
                IntPtr.Zero,
                IntPtr.Zero,
                CryptProtectUiForbidden,
                out output),
        "Windows could not protect the API credential for this user.");

    private static byte[] Unprotect(byte[] encrypted) => Transform(
        encrypted,
        static (ref DataBlob input, ref DataBlob entropy, out DataBlob output) =>
            CryptUnprotectData(
                ref input,
                IntPtr.Zero,
                ref entropy,
                IntPtr.Zero,
                IntPtr.Zero,
                CryptProtectUiForbidden,
                out output),
        "Windows could not unlock the saved API credential for this user.");

    private static byte[] Transform(byte[] inputBytes, CryptTransform transform, string errorMessage)
    {
        var inputPointer = Marshal.AllocHGlobal(inputBytes.Length);
        var entropyPointer = Marshal.AllocHGlobal(OptionalEntropy.Length);
        try
        {
            Marshal.Copy(inputBytes, 0, inputPointer, inputBytes.Length);
            Marshal.Copy(OptionalEntropy, 0, entropyPointer, OptionalEntropy.Length);
            var input = new DataBlob(inputBytes.Length, inputPointer);
            var entropy = new DataBlob(OptionalEntropy.Length, entropyPointer);
            if (!transform(ref input, ref entropy, out var output))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), errorMessage);
            }

            try
            {
                var result = new byte[output.Length];
                Marshal.Copy(output.Data, result, 0, output.Length);
                return result;
            }
            finally
            {
                if (output.Data != IntPtr.Zero)
                {
                    ZeroAndLocalFree(output.Data, output.Length);
                }
            }
        }
        finally
        {
            ZeroAndFree(inputPointer, inputBytes.Length);
            ZeroAndFree(entropyPointer, OptionalEntropy.Length);
        }
    }

    private static void ZeroAndFree(IntPtr pointer, int length)
    {
        if (pointer == IntPtr.Zero)
        {
            return;
        }

        for (var index = 0; index < length; index++)
        {
            Marshal.WriteByte(pointer, index, 0);
        }

        Marshal.FreeHGlobal(pointer);
    }

    private static void ZeroAndLocalFree(IntPtr pointer, int length)
    {
        if (pointer == IntPtr.Zero)
        {
            return;
        }

        for (var index = 0; index < length; index++)
        {
            Marshal.WriteByte(pointer, index, 0);
        }

        LocalFree(pointer);
    }

    private delegate bool CryptTransform(
        ref DataBlob input,
        ref DataBlob entropy,
        out DataBlob output);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct DataBlob(int length, IntPtr data)
    {
        public readonly int Length = length;
        public readonly IntPtr Data = data;
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string? description,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr promptStructure,
        int flags,
        out DataBlob dataOut);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        IntPtr description,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr promptStructure,
        int flags,
        out DataBlob dataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);
}

internal enum ApiCredentialSource
{
    None,
    EncryptedStore,
    Environment,
}

internal sealed record ApiCredentialLookup(string? ApiKey, ApiCredentialSource Source)
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);
}

internal sealed class ApiCredentialResolver(
    IApiCredentialStore store,
    Func<string, string?>? environmentReader = null)
{
    private readonly Func<string, string?> _environmentReader =
        environmentReader ?? Environment.GetEnvironmentVariable;

    public ApiCredentialLookup Resolve(LlmProvider provider)
    {
        var stored = store.Read(provider);
        if (!string.IsNullOrWhiteSpace(stored))
        {
            return new(stored, ApiCredentialSource.EncryptedStore);
        }

        var environmentName = LlmProviders.Get(provider).EnvironmentVariableName;
        var environmentValue = _environmentReader(environmentName);
        return string.IsNullOrWhiteSpace(environmentValue)
            ? new(null, ApiCredentialSource.None)
            : new(environmentValue, ApiCredentialSource.Environment);
    }
}
