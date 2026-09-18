using System.Security.Cryptography;

namespace CardiTrack.Infrastructure.Security;

/// <summary>
/// Mints and hashes the token that stands for a wearer's invitation.
/// </summary>
/// <remarks>
/// <para>
/// The token is the whole authorization for the anonymous wearer endpoints, so it is sized as a
/// credential rather than an identifier: 256 bits from the platform CSPRNG, base64url so it survives
/// a query string, an SMS, and a QR code without escaping.
/// </para>
/// <para>
/// <strong>Only the hash is ever stored.</strong> A plain SHA-256, and deliberately not a password
/// hash: stretching defends a low-entropy secret against a guessing attack, and there is nothing to
/// guess here — an attacker who could try a trillion tokens a second for a year would still be
/// nowhere. What a single unsalted hash does give, which bcrypt's per-row salt would take away, is
/// lookup by exact hash: one indexed read, no scan, and so nothing whose duration could vary with
/// how close a guess was.
/// </para>
/// </remarks>
public static class InviteTokens
{
    /// <summary>Bytes of randomness in a token. 32 is 256 bits.</summary>
    private const int TokenBytes = 32;

    /// <summary>
    /// The longest token the hasher will look at. A token is a known length; anything longer is
    /// someone probing, and there is no reason to spend a hash on it. Generous against the real
    /// thing (43 base64url characters) so no legitimate token is ever near the limit.
    /// </summary>
    internal const int MaxTokenLength = 256;

    /// <summary>A fresh token. Returned to the caregiver once and never stored.</summary>
    public static string Mint()
    {
        var bytes = RandomNumberGenerator.GetBytes(TokenBytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>
    /// The lower-case hex SHA-256 of a token, as stored. Null for anything that could not be a
    /// token — empty, whitespace, or improbably long — so a caller can refuse it without a database
    /// round trip.
    /// </summary>
    public static string? HashOrNull(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > MaxTokenLength)
            return null;

        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));
        return Convert.ToHexStringLower(hash);
    }
}
