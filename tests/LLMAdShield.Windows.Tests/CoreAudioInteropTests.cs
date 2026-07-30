using LLMAdShield.Windows.Platform.Audio;
using Xunit.Abstractions;

namespace LLMAdShield.Windows.Tests;

public sealed class CoreAudioInteropTests(ITestOutputHelper output)
{
    [Fact]
    public void ComInterfaceIdentifiers_MatchWindowsSdkContracts()
    {
        Assert.Equal(new Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), typeof(IMMDeviceEnumerator).GUID);
        Assert.Equal(new Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), typeof(IMMDeviceCollection).GUID);
        Assert.Equal(new Guid("D666063F-1587-4E43-81F1-B948E807363F"), typeof(IMMDevice).GUID);
        Assert.Equal(new Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), typeof(IAudioSessionManager2Native).GUID);
        Assert.Equal(new Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), typeof(IAudioSessionEnumeratorNative).GUID);
        Assert.Equal(new Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), typeof(IAudioSessionControl2Native).GUID);
        Assert.Equal(new Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), typeof(ISimpleAudioVolumeNative).GUID);
    }

    [Fact]
    public void Enumerate_CanQueryWindowsCoreAudio()
    {
        Assert.True(OperatingSystem.IsWindows());

        var sessions = new WindowsAudioSessionEnumerator().Enumerate();

        Assert.NotNull(sessions);
        output.WriteLine("Core Audio returned {0} session(s).", sessions.Count);
        foreach (var session in sessions)
        {
            output.WriteLine(session.SelectionDisplayName);
        }
    }
}
