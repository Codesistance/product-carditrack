using System.Security.Cryptography;
using System.Text;

namespace CardiTrack.Mobile.Core.Auth;

/// <summary>
/// RFC 7636 PKCE material plus the CSRF state token for the social-login code flow.
/// Client-side twin of Infrastructure/Security/PkceGenerator — Mobile.Core cannot
/// reference Infrastructure, so the two must stay in sync by hand.
/// All values are base64url-encoded (no padding) so they are URL-safe as issued.
/// </summary>
public static class Pkce
{
    public static string CreateVerifier() => RandomBase64Url(32);

    public static string CreateState() => RandomBase64Url(32);

    public static string CreateChallenge(string verifier) =>
        Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private static string RandomBase64Url(int byteCount) =>
        Base64Url(RandomNumberGenerator.GetBytes(byteCount));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
