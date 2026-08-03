using System.Security.Cryptography;

namespace CardiTrack.Infrastructure.Security;

/// <summary>
/// RFC 7636 PKCE material plus the CSRF state token for OAuth authorization-code flows.
/// All values are base64url-encoded (no padding) so they are URL-safe as issued.
/// </summary>
public static class PkceGenerator
{
    public static string GenerateCodeVerifier() => RandomBase64Url(32);

    public static string GenerateStateToken() => RandomBase64Url(32);

    public static string GenerateCodeChallenge(string codeVerifier)
    {
        var hash = SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(codeVerifier));
        return Base64Url(hash);
    }

    private static string RandomBase64Url(int byteCount)
    {
        var bytes = RandomNumberGenerator.GetBytes(byteCount);
        return Base64Url(bytes);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
