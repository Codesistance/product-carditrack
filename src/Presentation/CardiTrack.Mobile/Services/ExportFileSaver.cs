using CardiTrack.Mobile.Core.Api;

namespace CardiTrack.Mobile.Services;

/// <summary>Where a saved export landed, in the caregiver's words, or why it did not.</summary>
/// <param name="Where">The place to name back to them ("Downloads", "Files, under CardiTrack"),
/// or null when the save failed.</param>
public sealed record ExportSaved(bool Ok, string? Where);

/// <summary>
/// Keeps a finished export on this phone, where the operating system keeps files.
/// </summary>
/// <remarks>
/// <para>
/// This exists so that "Save" and "Share" can be two honest choices. The share sheet does hide a
/// save inside it — "Save to Files" on iOS, a Files target on Android — but a caregiver who
/// wanted a copy on their phone had to know that, and the chooser this replaced offered them
/// "Save or share" and "Open" as if those were three different things.
/// </para>
/// <para>
/// A cache file is not a save: the cache is swept by the next export (and by the OS under
/// pressure), and nothing outside this app can see it. So each platform writes to the place its
/// own file manager reads.
/// </para>
/// </remarks>
public interface IExportFileSaver
{
    /// <summary>Whether this platform can keep a file where the caregiver will find it again.
    /// False hides the Save tile rather than offering a choice that would do nothing.</summary>
    bool IsSupported { get; }

    /// <summary>Where Save will put it, for the tile to say before they choose.</summary>
    string PlaceName { get; }

    /// <summary>
    /// Writes the export where <see cref="PlaceName"/> says. Never throws: a save that fails
    /// comes back false, and the caller offers the share sheet, which is the one route off the
    /// phone that always exists.
    /// </summary>
    Task<ExportSaved> SaveAsync(ReportFile file, CancellationToken ct = default);
}

public sealed class ExportFileSaver : IExportFileSaver
{
#if ANDROID

    // MediaStore, not a path. Since scoped storage (API 29; this app's floor is 31) the public
    // Downloads folder is not writable by path, and the legacy external-storage permission is
    // not granted to apps targeting it. An insert into MediaStore.Downloads needs no permission
    // at all, and the file it produces is the one the phone's Files app lists under Downloads.
    public bool IsSupported => true;

    public string PlaceName => "Downloads";

    public async Task<ExportSaved> SaveAsync(ReportFile file, CancellationToken ct = default)
    {
        try
        {
            var values = new Android.Content.ContentValues();
            values.Put(Android.Provider.MediaStore.IMediaColumns.DisplayName, file.FileName);
            values.Put(Android.Provider.MediaStore.IMediaColumns.MimeType, file.ContentType);
            values.Put(
                Android.Provider.MediaStore.IMediaColumns.RelativePath,
                Android.OS.Environment.DirectoryDownloads);

            var resolver = Android.App.Application.Context.ContentResolver;
            if (resolver is null)
                return new ExportSaved(false, null);

            // MediaStore gives the file a "(1)" suffix rather than overwriting a name already
            // there, which is what a caregiver exporting the same conversation twice expects.
            var uri = resolver.Insert(Android.Provider.MediaStore.Downloads.ExternalContentUri, values);
            if (uri is null)
                return new ExportSaved(false, null);

            await using var output = resolver.OpenOutputStream(uri, "w");
            if (output is null)
                return new ExportSaved(false, null);

            await output.WriteAsync(file.Content, ct);
            await output.FlushAsync(ct);
            return new ExportSaved(true, PlaceName);
        }
        catch (Exception ex)
        {
            // A save that fails must not cost the caregiver the export: the caller falls back to
            // the share sheet, which reaches the same Files app by a longer road.
            ScreenRefresh.LogFailure(ex, nameof(ExportFileSaver), "while saving an export");
            return new ExportSaved(false, null);
        }
    }

#elif IOS || MACCATALYST

    // iOS has no Downloads folder. The app's own Documents directory is the equivalent, and it
    // is listed in Files under the app's name because Info.plist sets UIFileSharingEnabled and
    // LSSupportsOpeningDocumentsInPlace — without those two keys this directory is private and
    // a "save" here would be invisible, which is the failure this class exists to avoid.
    public bool IsSupported => true;

    public string PlaceName => "Files, under CardiTrack";

    public async Task<ExportSaved> SaveAsync(ReportFile file, CancellationToken ct = default)
    {
        try
        {
            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            Directory.CreateDirectory(documents);
            await File.WriteAllBytesAsync(Path.Combine(documents, file.FileName), file.Content, ct);
            return new ExportSaved(true, PlaceName);
        }
        catch (Exception ex)
        {
            ScreenRefresh.LogFailure(ex, nameof(ExportFileSaver), "while saving an export");
            return new ExportSaved(false, null);
        }
    }

#else

    // Windows and the designer. Nothing here can promise a caregiver-visible folder, so the Save
    // tile is hidden rather than offered and then apologised for.
    public bool IsSupported => false;

    public string PlaceName => string.Empty;

    public Task<ExportSaved> SaveAsync(ReportFile file, CancellationToken ct = default) =>
        Task.FromResult(new ExportSaved(false, null));

#endif
}
