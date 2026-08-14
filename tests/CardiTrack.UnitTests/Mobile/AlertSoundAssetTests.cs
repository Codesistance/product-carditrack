using System.Text;
using CardiTrack.Application.Services.Notifications;

namespace CardiTrack.UnitTests.Mobile;

/// <summary>
/// iOS and Android each need their own copy of the alert chime (different bundle layouts).
/// They must stay the same file, or caregivers hear a different pager per platform.
/// </summary>
public class AlertSoundAssetTests
{
    [Fact]
    public void BundledAlertSounds_AreIdenticalShortPcmWavs()
    {
        var root = FindRepoRoot();
        var ios = Path.Combine(root, "src", "Presentation", "CardiTrack.Mobile",
            "Platforms", "iOS", "Resources", NotificationChannels.AlertSoundFile);
        var android = Path.Combine(root, "src", "Presentation", "CardiTrack.Mobile",
            "Platforms", "Android", "Resources", "raw", NotificationChannels.AlertSoundFile);

        Assert.True(File.Exists(ios), $"Missing iOS alert sound at {ios}");
        Assert.True(File.Exists(android), $"Missing Android alert sound at {android}");

        var bytes = File.ReadAllBytes(ios);
        Assert.Equal(bytes, File.ReadAllBytes(android));

        Assert.True(bytes.Length >= 44, "WAV header is at least 44 bytes.");
        Assert.Equal("RIFF", Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(bytes, 8, 4));

        var audioFormat = BitConverter.ToUInt16(bytes, 20);
        var channels = BitConverter.ToUInt16(bytes, 22);
        var sampleRate = BitConverter.ToInt32(bytes, 24);
        var byteRate = BitConverter.ToInt32(bytes, 28);
        var bitsPerSample = BitConverter.ToUInt16(bytes, 34);

        Assert.Equal(1, audioFormat); // linear PCM — Apple's custom-sound requirement
        Assert.Equal(1, channels);
        Assert.Equal(44100, sampleRate);
        Assert.Equal(16, bitsPerSample);

        var durationSeconds = (bytes.Length - 44) / (double)byteRate;
        Assert.InRange(durationSeconds, 0.3, 5.0); // distinctive, well under Apple's 30s cap
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CardiTrack.sln")))
                return dir.FullName;
        }

        throw new InvalidOperationException(
            $"Could not find CardiTrack.sln walking up from {AppContext.BaseDirectory}.");
    }
}
