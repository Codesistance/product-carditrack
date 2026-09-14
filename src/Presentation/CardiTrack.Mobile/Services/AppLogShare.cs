using System.IO.Compression;
using System.Text;
using Serilog;

namespace CardiTrack.Mobile.Services;

/// <summary>
/// Hands this phone's log files to the share sheet, so what the app recorded before a crash can
/// be read without the device being plugged into a developer's machine.
/// </summary>
/// <remarks>
/// <para>
/// The alternative on iOS is Xcode's Download Container, which needs the physical handset and a
/// Mac. <c>UIFileSharingEnabled</c> is no substitute: it exposes Documents, and
/// <see cref="AppLogging.LogDirectory"/> sits under Library. A share sheet is the only route off
/// the device that the person holding it can take on their own.
/// </para>
/// <para>
/// Nothing is transmitted by this class — the caregiver chooses the destination in the sheet,
/// exactly as the health-data export does. That is also why it is not gated on
/// <see cref="DiagnosticsConsent"/>: that toggle governs what the app sends of its own accord,
/// and this is a deliberate act by the person whose phone it is.
/// </para>
/// </remarks>
public static class AppLogShare
{
    /// <summary>Marks our staged archives out from everything else in the cache directory.</summary>
    private const string ZipPrefix = "carditrack-logs-";

    /// <summary>
    /// Stages the logs as one zip and opens the share sheet. False means there was nothing to
    /// share: the file sink only records Warning and above, so a phone that has had no trouble
    /// has no log file at all — worth saying out loud rather than opening an empty sheet over.
    /// </summary>
    public static async Task<bool> TryShareAsync(CancellationToken ct = default)
    {
        var logs = Directory.Exists(AppLogging.LogDirectory)
            ? Directory.GetFiles(AppLogging.LogDirectory, "*.log")
            : [];
        if (logs.Length == 0)
            return false;

        // Local time in the name, not UTC: this is read by whoever was holding the phone, and
        // it is the only part of the bundle they can match against "it crashed around four".
        var path = Path.Combine(
            FileSystem.CacheDirectory,
            $"{ZipPrefix}{DateTime.Now:yyyyMMdd-HHmmss}.zip");

        ClearPreviousZips();
        await WriteZipAsync(path, logs, ct);

        await Share.Default.RequestAsync(new ShareFileRequest
        {
            Title = "Send app logs",
            File = new ShareFile(path),
        });
        return true;
    }

    /// <summary>
    /// Copies each file in through a shared-read handle rather than letting
    /// <see cref="ZipFile"/> open them itself. Serilog holds the current day's file open for
    /// writing, and an exclusive open would fail on precisely the log that matters most.
    /// </summary>
    private static async Task WriteZipAsync(
        string path, IReadOnlyList<string> logs, CancellationToken ct)
    {
        using var zip = new ZipArchive(File.Create(path), ZipArchiveMode.Create);

        var about = zip.CreateEntry("about.txt", CompressionLevel.Optimal);
        await using (var text = new StreamWriter(about.Open(), Encoding.UTF8))
            await text.WriteAsync(Describe());

        foreach (var log in logs)
        {
            var entry = zip.CreateEntry(Path.GetFileName(log), CompressionLevel.Optimal);
            await using var source = new FileStream(
                log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            await using var target = entry.Open();
            await source.CopyToAsync(target, ct);
        }
    }

    /// <summary>
    /// Which build wrote the log, and on what. Every line already carries the version, but the
    /// build number, the OS release and the hardware model are what a crash report has to be
    /// matched against, and none of the three appear in the log itself.
    /// </summary>
    private static string Describe() =>
        $"""
        App:      {AppInfo.Current.VersionString} ({AppInfo.Current.BuildString})
        Package:  {AppInfo.Current.PackageName}
        Device:   {DeviceInfo.Current.Manufacturer} {DeviceInfo.Current.Model}
        Platform: {DeviceInfo.Current.Platform} {DeviceInfo.Current.VersionString}
        Captured: {DateTimeOffset.Now:u}
        """;

    /// <summary>
    /// An earlier zip has already been shared or abandoned; either way it is a duplicate copy of
    /// the same log sitting in the cache. Failures are swallowed — not being able to tidy up must
    /// not stop the caregiver sending today's.
    /// </summary>
    private static void ClearPreviousZips()
    {
        try
        {
            foreach (var stale in Directory.GetFiles(FileSystem.CacheDirectory, $"{ZipPrefix}*.zip"))
                File.Delete(stale);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "AppLogShare: could not clear the previously staged log archives.");
        }
    }
}
