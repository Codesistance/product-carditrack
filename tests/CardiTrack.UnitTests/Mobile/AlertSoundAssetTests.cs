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

        var fmt = RequireChunk(bytes, "fmt ");
        var data = RequireChunk(bytes, "data");

        var audioFormat = BitConverter.ToUInt16(fmt, 0);
        var channels = BitConverter.ToUInt16(fmt, 2);
        var sampleRate = BitConverter.ToInt32(fmt, 4);
        var bitsPerSample = BitConverter.ToUInt16(fmt, 14);

        Assert.Equal(1, audioFormat); // linear PCM — Apple's custom-sound requirement
        Assert.InRange(channels, 1, 2);
        Assert.Equal(44100, sampleRate);
        Assert.Equal(16, bitsPerSample);

        var bytesPerSec = sampleRate * channels * (bitsPerSample / 8);
        var durationSeconds = data.Length / (double)bytesPerSec;
        Assert.InRange(durationSeconds, 0.3, 30.0); // Apple rejects custom sounds over 30s
    }

    private static byte[] RequireChunk(byte[] wav, string fourCc)
    {
        var offset = 12;
        while (offset + 8 <= wav.Length)
        {
            var id = Encoding.ASCII.GetString(wav, offset, 4);
            var size = BitConverter.ToInt32(wav, offset + 4);
            var start = offset + 8;
            if (size < 0 || start + size > wav.Length)
                break;
            if (id == fourCc)
                return wav.AsSpan(start, size).ToArray();
            offset = start + size + (size & 1);
        }

        throw new InvalidOperationException($"WAV is missing a {fourCc} chunk.");
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
