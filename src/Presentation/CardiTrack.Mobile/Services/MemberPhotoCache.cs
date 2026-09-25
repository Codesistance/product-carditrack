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
    private static readonly ConcurrentDictionary<string, Task<string?>> InFlight = new();

    private static string Root => Path.Combine(FileSystem.CacheDirectory, "member-photos");

    /// <summary>The saved file for this photo, if it is already on the phone.</summary>
    public static string? Cached(MemberPhotoCacheKey key)
    {
        var path = PathFor(key);
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Downloads the photo and saves it under its key, replacing that member's older photo.
    /// Null when it could not be fetched — the avatar keeps its initials rather than failing.
    /// </summary>
    public static Task<string?> FetchAsync(Uri url, MemberPhotoCacheKey key) =>
        InFlight.GetOrAdd(PathFor(key), _ => FetchCoreAsync(url, key));

    private static async Task<string?> FetchCoreAsync(Uri url, MemberPhotoCacheKey key)
    {
        var path = PathFor(key);
        var folder = Path.GetDirectoryName(path)!;
        var temp = path + ".part";
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

            // Written aside and moved into place, so a half-finished download is never read as
            // the photo.
            File.Move(temp, path, overwrite: true);

            // The member has one photo: anything else in their folder is the one this replaced.
            if (key.Folder != MemberPhotoCacheKey.SharedFolder)
            {
                foreach (var stale in Directory.EnumerateFiles(folder).Where(f => f != path))
                    TryDelete(stale);
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
        if (Directory.Exists(Root))
            Directory.Delete(Root, recursive: true);
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
