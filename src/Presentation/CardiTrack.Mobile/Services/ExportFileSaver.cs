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
/// wanted a copy on their phone had to know that, and the chooser this replaced put it behind
/// one button reading "Save or share", which named two actions and performed one.
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
        var resolver = Android.App.Application.Context.ContentResolver;
        if (resolver is null)
            return new ExportSaved(false, null);

        Android.Net.Uri? uri = null;
        try
        {
            var values = new Android.Content.ContentValues();
            values.Put(Android.Provider.MediaStore.IMediaColumns.DisplayName, file.FileName);
            values.Put(Android.Provider.MediaStore.IMediaColumns.MimeType, file.ContentType);
            values.Put(
                Android.Provider.MediaStore.IMediaColumns.RelativePath,
                Android.OS.Environment.DirectoryDownloads);

            // Inserted pending, published below once every byte is down. Without this the row is
            // visible to Files and to the media scanner from the moment it is created, so a
            // caregiver who taps straight into Downloads can open a truncated health record that
            // looks exactly like their export. It also means a write that dies half way leaves a
            // row the system reaps rather than a corrupt file sitting in Downloads.
            values.Put(Android.Provider.MediaStore.IMediaColumns.IsPending, 1);

            // MediaStore gives the file a "(1)" suffix rather than overwriting a name already
            // there, which is what a caregiver exporting the same conversation twice expects.
            uri = resolver.Insert(Android.Provider.MediaStore.Downloads.ExternalContentUri, values);
            if (uri is null)
                return new ExportSaved(false, null);

            // Closed inside the try, before the row is published: "saved" has to mean the bytes
            // are down, not that they are on their way.
            await using (var output = resolver.OpenOutputStream(uri, "w"))
            {
                if (output is null)
                    return new ExportSaved(false, null);

                await output.WriteAsync(file.Content, ct);
                await output.FlushAsync(ct);
            }

            var published = new Android.Content.ContentValues();
            published.Put(Android.Provider.MediaStore.IMediaColumns.IsPending, 0);
            resolver.Update(uri, published, null, null);

            return new ExportSaved(true, PlaceName);
        }
        catch (Exception ex)
        {
            // A save that fails must not cost the caregiver the export: the caller falls back to
            // the share sheet, which reaches the same Files app by a longer road.
            ScreenRefresh.LogFailure(ex, nameof(ExportFileSaver), "while saving an export");
            Discard(resolver, uri);
            return new ExportSaved(false, null);
        }
    }

    /// <summary>
    /// Drops a row whose write did not finish. The system clears abandoned pending rows on its
    /// own eventually; taking ours now means a failed save leaves nothing behind at all.
    /// </summary>
    private static void Discard(Android.Content.ContentResolver resolver, Android.Net.Uri? uri)
    {
        if (uri is null)
            return;

        try
        {
            resolver.Delete(uri, null, null);
        }
        catch (Exception)
        {
            // Already gone, or not ours to remove. The pending row expires either way.
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
