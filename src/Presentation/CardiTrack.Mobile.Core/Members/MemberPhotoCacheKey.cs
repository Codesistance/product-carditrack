using System.Security.Cryptography;
using System.Text;

namespace CardiTrack.Mobile.Core.Members;

/// <summary>
/// Where a member's photo is kept on the phone: a folder per member and a file named for the
/// photo's storage object, so the same photo is downloaded once and a new one replaces it.
/// </summary>
/// <remarks>
/// <para>
/// Keyed on the URL's host and path, never its query. A photo URL is a signed link whose query —
/// the signature and its expiry — changes every few minutes while the object it points at does
/// not, so keying on the whole URL (what the image loader's own cache does) missed every time the
/// link was re-signed: the avatar fell back to initials and downloaded the same photo again.
/// </para>
/// <para>
/// That is only safe because the server never overwrites a photo in place: every upload is a new
/// object, <c>members/{memberId}/{random}.jpg</c>. A new photo is a new path, so a new key.
/// </para>
/// </remarks>
public sealed record MemberPhotoCacheKey(string Folder, string FileName)
{
    /// <summary>The folder for a photo whose path does not name its member.</summary>
    public const string SharedFolder = "shared";

    /// <summary>The key for a photo URL, or null for one that is not an absolute http(s) URL.</summary>
    public static MemberPhotoCacheKey? For(Uri? url)
    {
        if (url is null || !url.IsAbsoluteUri || (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp))
            return null;

        var path = url.AbsolutePath;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url.Host + path)))[..32]
            .ToLowerInvariant();
        var extension = Path.GetExtension(path) is { Length: > 1 and <= 5 } ext ? ext.ToLowerInvariant() : ".img";

        return new MemberPhotoCacheKey(MemberFolder(path) ?? SharedFolder, hash + extension);
    }

    /// <summary>
    /// The member id from a <c>…/members/{id}/…</c> path, so a member's newer photo can clear out
    /// the older one — or null when the path does not carry one.
    /// </summary>
    private static string? MemberFolder(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length - 2; i++)
        {
            if (segments[i] == "members" && Guid.TryParse(segments[i + 1], out var id))
                return id.ToString("N");
        }

        return null;
    }
}
