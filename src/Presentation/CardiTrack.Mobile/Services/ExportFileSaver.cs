using CardiTrack.Mobile.Core.Api;

namespace CardiTrack.Mobile.Services;

/// <summary>Where a saved export landed, in the caregiver's words, or why it did not.</summary>
/// <param name="Where">The place to name back to them ("Downloads", "Files, under CardiTrack"),
/// or null when the save failed.</param>
/// <param name="FileName">
/// The name the file actually ended up under, which is not always the one asked for: neither
/// platform overwrites a name already taken, so a second export of the same conversation lands
/// beside the first. Null when the save failed. The caregiver is told this one — naming the
/// requested file would send them looking for something that is not there.
/// </param>
public sealed record ExportSaved(bool Ok, string? Where, string? FileName = null);

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

            return new ExportSaved(true, PlaceName, SavedNameOf(resolver, uri) ?? file.FileName);
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
    /// What MediaStore settled on. It suffixes "(1)" rather than overwriting a name already in
    /// Downloads, so the name asked for and the name written are not always the same — and the
    /// caregiver is about to be told where to look. Null if the row will not answer, and the
    /// caller falls back to the requested name.
    /// </summary>
    private static string? SavedNameOf(Android.Content.ContentResolver resolver, Android.Net.Uri uri)
    {
        try
        {
            using var cursor = resolver.Query(
                uri, [Android.Provider.MediaStore.IMediaColumns.DisplayName], null, null, null);
            return cursor?.MoveToFirst() == true ? cursor.GetString(0) : null;
        }
        catch (Exception)
        {
            return null;
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

            // Never over the top of one already there. Plain WriteAllBytes would replace it, and
            // a caregiver who exported the same conversation twice would be told both were saved
            // while holding one — silent loss of a health record, and the opposite of what the
            // Android half does, which leaves the earlier copy alone and suffixes the new one.
            var name = FreeNameIn(documents, file.FileName);
            await File.WriteAllBytesAsync(Path.Combine(documents, name), file.Content, ct);
            return new ExportSaved(true, PlaceName, name);
        }
        catch (Exception ex)
        {
            ScreenRefresh.LogFailure(ex, nameof(ExportFileSaver), "while saving an export");
            return new ExportSaved(false, null);
        }
    }

    /// <summary>
    /// <paramref name="name"/> if nothing in <paramref name="directory"/> holds it, otherwise the
    /// same name with " (1)", " (2)" … before the extension — the shape MediaStore uses on the
    /// other platform, so a caregiver switching phones sees the same convention.
    /// </summary>
    /// <remarks>
    /// The check and the write are not atomic, which is a race this app cannot lose: the only
    /// writer to this directory is an export, and one caregiver cannot tap Save on two of them at
    /// the same instant. The ceiling is there so a directory in a state nobody expects returns a
    /// name rather than spinning.
    /// </remarks>
    private static string FreeNameIn(string directory, string name)
    {
        if (!File.Exists(Path.Combine(directory, name)))
            return name;

        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);

        for (var suffix = 1; suffix < 1000; suffix++)
        {
            var candidate = $"{stem} ({suffix}){extension}";
            if (!File.Exists(Path.Combine(directory, candidate)))
                return candidate;
        }

        return name;
    }

#else

    // Windows and the designer. Nothing here can promise a caregiver-visible folder, so the Save
    // tile is hidden rather than offered and then apologised for.
    public bool IsSupported => false;

    public string PlaceName => string.Empty;

    public Task<ExportSaved> SaveAsync(ReportFile file, CancellationToken ct = default) =>
        Task.FromResult(new ExportSaved(false, null, null));

#endif
}
