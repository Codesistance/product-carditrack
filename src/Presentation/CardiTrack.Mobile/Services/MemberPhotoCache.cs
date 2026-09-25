using System.Collections.Concurrent;
using CardiTrack.Mobile.Core.Members;

namespace CardiTrack.Mobile.Services;

/// <summary>
/// Keeps each CardiMember's photo on the phone and downloads it only when it has changed — see
/// <see cref="MemberPhotoCacheKey"/> for why the key is the photo's storage path and not its
/// signed URL.
/// </summary>
/// <remarks>
/// <para>
/// In the app's private cache directory: out of backups and other apps' reach, and the OS may
/// reclaim it under storage pressure, which costs no more than one download. A full-face photo is
/// sensitive, so <see cref="Clear"/> runs at sign-out and account deletion with the rest of what a
/// session leaves behind.
/// </para>
/// <para>
/// One download per photo at a time: the dashboard, the Family tab and Alert Details can all ask
/// for the same member at once, and they share the one fetch in flight.
/// </para>
/// </remarks>
public static class MemberPhotoCache
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>
    /// Lazy so only one download can start per photo: <c>GetOrAdd</c> may run its factory more than
    /// once when two avatars ask at the same moment, and a factory that started the fetch itself
    /// would have both racing into the same file. Only the stored Lazy's value is ever run.
    /// </summary>
    private static readonly ConcurrentDictionary<string, Lazy<Task<string?>>> InFlight = new();

    /// <summary>
    /// Moves on at every <see cref="Clear"/>. A download that started before a sign-out must not
    /// land after it and leave the last account's photo on the phone, so the move into place
    /// happens under <see cref="Gate"/> and only while the generation it started in still holds.
    /// </summary>
    private static int _generation;
    private static readonly object Gate = new();

    /// <summary>
    /// The photo each member folder was most recently asked for — the member's current photo, as
    /// far as the phone knows. Only that one may clear the folder when it lands: a download of the
    /// photo it replaced can still be in flight (a screen that loaded before the change) and
    /// finish afterwards, and without this it would delete the newer photo it lost the race to.
    /// </summary>
    private static readonly ConcurrentDictionary<string, string> Latest = new();

    private static string Root => Path.Combine(FileSystem.CacheDirectory, "member-photos");

    /// <summary>The saved file for this photo, if it is already on the phone.</summary>
    public static string? Cached(MemberPhotoCacheKey key)
    {
        var path = PathFor(key);
        Latest[Path.GetDirectoryName(path)!] = path;
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Downloads the photo and saves it under its key, replacing that member's older photo.
    /// Null when it could not be fetched — the avatar keeps its initials rather than failing.
    /// </summary>
    public static Task<string?> FetchAsync(Uri url, MemberPhotoCacheKey key)
    {
        var path = PathFor(key);
        Latest[Path.GetDirectoryName(path)!] = path;
        return InFlight.GetOrAdd(path, _ => new Lazy<Task<string?>>(() => FetchCoreAsync(url, key))).Value;
    }

    private static async Task<string?> FetchCoreAsync(Uri url, MemberPhotoCacheKey key)
    {
        var path = PathFor(key);
        var folder = Path.GetDirectoryName(path)!;
        var temp = path + ".part";
        var generation = Volatile.Read(ref _generation);
        try
        {
            Directory.CreateDirectory(folder);
            using (var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
            {
                if (!response.IsSuccessStatusCode)
                    return null;

                await using var file = File.Create(temp);
                await response.Content.CopyToAsync(file);
            }

            lock (Gate)
            {
                // Signed out while this was downloading: the photo belongs to a session that has
                // ended, so it is thrown away rather than kept for whoever signs in next.
                if (generation != _generation)
                {
                    TryDelete(temp);
                    return null;
                }

                // Written aside and moved into place, so a half-finished download is never read
                // as the photo.
                File.Move(temp, path, overwrite: true);

                // The member has one photo: anything else in their folder is the one this replaced
                // — but only when this is the photo the folder was last asked for. An older one
                // landing late keeps its own file for the screen that asked, and leaves the
                // clearing to the current photo.
                if (key.Folder != MemberPhotoCacheKey.SharedFolder
                    && Latest.TryGetValue(folder, out var latest) && latest == path)
                {
                    foreach (var stale in Directory.EnumerateFiles(folder).Where(f => f != path))
                        TryDelete(stale);
                }
            }

            return path;
        }
        catch (Exception)
        {
            // Offline, a link past its expiry, a full disk: all leave the initials showing, and
            // the next screen to ask tries again with a fresh link.
            TryDelete(temp);
            return null;
        }
        finally
        {
            InFlight.TryRemove(path, out _);
        }
    }

    /// <summary>Deletes every saved photo. Run at sign-out and at account deletion.</summary>
    public static void Clear()
    {
        lock (Gate)
        {
            // First, so a download finishing while the folder is being deleted is discarded
            // rather than moved into a folder it would recreate.
            _generation++;
            // Which photo is whose is the old session's knowledge too.
            Latest.Clear();
            try
            {
                if (Directory.Exists(Root))
                    Directory.Delete(Root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Never the reason a sign-out stops. The generation has moved, so nothing from the
                // old session is written from here on; what is left sits in the app's private cache
                // directory, which the OS reclaims under pressure.
            }
        }
    }

    private static string PathFor(MemberPhotoCacheKey key) => Path.Combine(Root, key.Folder, key.FileName);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // In use by an image being drawn; the next replacement clears it.
        }
    }
}
