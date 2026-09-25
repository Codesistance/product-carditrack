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
/// session leaves behind — and each session has its own folder, so a file the delete cannot
/// remove is never shown to the next account (see <see cref="SessionKey"/>).
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

    private static string Base => Path.Combine(FileSystem.CacheDirectory, "member-photos");

    /// <summary>
    /// Which session's folder under <see cref="Base"/> is in use. Photos only ever go into, and
    /// are only ever read from, the current session's folder; <see cref="Clear"/> starts a new one.
    /// A folder the delete could not remove (a file still held open by an image being drawn) is
    /// therefore never served to the next account, and is swept on the next start or sign-out.
    /// </summary>
    private const string SessionKey = "MemberPhotoCacheSession";

    private static string? _session;
    private static int _swept;

    private static string Root
    {
        get
        {
            var session = _session ??= Preferences.Default.Get(SessionKey, string.Empty) is { Length: > 0 } saved
                ? saved
                : NewSession();
            // Once per run: anything an earlier sign-out could not delete goes now, while nothing
            // from those sessions is being drawn.
            if (Interlocked.Exchange(ref _swept, 1) == 0)
                SweepOtherSessions(session);
            return Path.Combine(Base, session);
        }
    }

    private static string NewSession()
    {
        var session = Guid.NewGuid().ToString("N");
        Preferences.Default.Set(SessionKey, session);
        return session;
    }

    private static void SweepOtherSessions(string keep)
    {
        try
        {
            if (!Directory.Exists(Base))
                return;
            foreach (var folder in Directory.EnumerateDirectories(Base))
            {
                if (!string.Equals(Path.GetFileName(folder), keep, StringComparison.Ordinal))
                    TryDeleteFolder(folder);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Retried at the next start or sign-out; none of it can be served meanwhile.
        }
    }

    private static void TryDeleteFolder(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Held open; the next sweep takes it.
        }
    }

    /// <summary>The saved file for this photo, if it is already on the phone.</summary>
    /// <remarks>
    /// A lookup, not a claim: it leaves <see cref="Latest"/> alone. An avatar still holding the
    /// member's old link would otherwise mark the old photo current just by being drawn, and the
    /// old download landing afterwards would then clear the new photo away.
    /// </remarks>
    public static string? Cached(MemberPhotoCacheKey key)
    {
        var path = PathFor(key);
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Downloads the photo and saves it under its key, replacing that member's older photo.
    /// Null when it could not be fetched — the avatar keeps its initials rather than failing.
    /// </summary>
    public static Task<string?> FetchAsync(Uri url, MemberPhotoCacheKey key)
    {
        // The session's folder and the session's generation are read together, under the lock
        // Clear() takes: read apart, a sign-out between them could pair the old session's folder
        // with the new generation, and the download would then pass the check that is meant to
        // throw it away and write the last account's photo back after the sign-out.
        string path;
        int generation;
        lock (Gate)
        {
            path = PathFor(key);
            generation = _generation;
            Latest[Path.GetDirectoryName(path)!] = path;
        }

        // Keyed by session as well as photo: a download begun before a sign-out is thrown away
        // when it lands, and a request after the sign-out must start its own rather than join
        // that one and get nothing back.
        var flight = $"{generation}|{path}";
        return InFlight.GetOrAdd(flight, _ => new Lazy<Task<string?>>(() => FetchCoreAsync(url, key, path, generation, flight))).Value;
    }

    private static async Task<string?> FetchCoreAsync(
        Uri url, MemberPhotoCacheKey key, string path, int generation, string flight)
    {
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
                // — but only when this is the photo the folder was last asked for.
                if (key.Folder != MemberPhotoCacheKey.SharedFolder
                    && Latest.TryGetValue(folder, out var latest))
                {
                    if (latest == path)
                    {
                        foreach (var stale in Directory.EnumerateFiles(folder).Where(f => f != path))
                            TryDelete(stale);
                    }
                    else if (File.Exists(latest))
                    {
                        // An older photo landing after the current one already has: nothing is
                        // left to clear it, so it goes now, and the screen that asked for it gets
                        // the current photo instead. Had the current one not landed yet, this
                        // file stays, and the current photo's own clearing takes it.
                        TryDelete(path);
                        return latest;
                    }
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
            InFlight.TryRemove(flight, out _);
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

            // A new folder before the old one is deleted, so whatever the delete cannot remove is
            // already out of reach: nothing reads from, or writes to, any folder but the current
            // session's. Never the reason a sign-out stops.
            _session = NewSession();
            SweepOtherSessions(_session);
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
