using System.Security.Cryptography;
using System.Text;
using CardiTrack.Application.Interfaces.Security;
using CardiTrack.Shared;

namespace CardiTrack.Infrastructure.Security;

/// <summary>
/// Issues and validates the single-use, stateless ack/fetch tokens (notification_engine.md §7.2
/// C3/C5) — HMAC-SHA256 over <c>deliveryId | pushDeviceTokenId | expiresAt</c>, keyed by a secret
/// held in Secret Manager. Not related to <see cref="AesEncryptionService"/> beyond sharing a key-
/// validation discipline: there is no ciphertext here, just a MAC, so validation is a constant-
/// time comparison, never a decrypt.
/// </summary>
/// <remarks>
/// Token format is <c>{pushDeviceTokenId:N}.{expiresAtUnixSeconds}.{base64url(hmac)}</c> — the
/// token carries <c>pushDeviceTokenId</c> and its expiry in the clear (neither is secret; both
/// are already visible to whoever the push was sent to), and the HMAC — computed over
/// <c>deliveryId</c> (supplied by the caller from the URL, not the token) plus those two fields —
/// is what an attacker cannot forge without the key. Tampering with <c>pushDeviceTokenId</c> or
/// <c>expiresAt</c> changes the HMAC input, so the recomputed MAC no longer matches. Expiry is
/// checked before the constant-time compare, so a stale or replayed token is rejected cheaply.
/// The "fetch" and "ack" tokens use the same construction with different HMAC domain-separation
/// prefixes, so a fetch token can never validate as an ack token or vice versa (§7.2 C5 —
/// "useless for any other call").
/// </remarks>
public class AckTokenService : IAckTokenService
{
    private const string AckDomain = "ack";
    private const string FetchDomain = "fetch";
    private const int KeySize = 32;

    private static readonly HashSet<string> Placeholders = new(StringComparer.OrdinalIgnoreCase)
    {
        "REPLACE_ME"
    };

    private readonly byte[] _key;

    public AckTokenService(string base64Key)
    {
        var key = base64Key?.Trim();

        if (string.IsNullOrEmpty(key) || Placeholders.Contains(key))
            throw new ArgumentException(
                $"'{ConfigurationKeys.Notifications.AckTokenKey}' is not set. Provide a base64-encoded " +
                $"256-bit key via environment variable " +
                $"'{ConfigurationLoader.ToEnvVarKey(ConfigurationKeys.Notifications.AckTokenKey)}' or configuration.",
                nameof(base64Key));

        var buffer = new byte[KeySize];
        if (!Convert.TryFromBase64String(key, buffer, out var written) || written != KeySize)
            throw new ArgumentException(
                $"'{ConfigurationKeys.Notifications.AckTokenKey}' must be a base64-encoded 256-bit " +
                $"({KeySize}-byte) key. The configured value is not valid base64 or does not decode to that length.",
                nameof(base64Key));

        _key = buffer;
    }

    public string IssueAckToken(Guid deliveryId, Guid pushDeviceTokenId, DateTime expiresAt) =>
        Issue(AckDomain, deliveryId, pushDeviceTokenId, expiresAt);

    public bool ValidateAckToken(string token, Guid deliveryId, out Guid pushDeviceTokenId) =>
        Validate(AckDomain, token, deliveryId, out pushDeviceTokenId);

    public string IssueFetchToken(Guid deliveryId, Guid pushDeviceTokenId, DateTime expiresAt) =>
        Issue(FetchDomain, deliveryId, pushDeviceTokenId, expiresAt);

    public bool ValidateFetchToken(string token, Guid deliveryId, out Guid pushDeviceTokenId) =>
        Validate(FetchDomain, token, deliveryId, out pushDeviceTokenId);

    private string Issue(string domain, Guid deliveryId, Guid pushDeviceTokenId, DateTime expiresAt)
    {
        var expiresAtUnixSeconds = new DateTimeOffset(DateTime.SpecifyKind(expiresAt, DateTimeKind.Utc)).ToUnixTimeSeconds();
        var mac = Compute(domain, deliveryId, pushDeviceTokenId, expiresAtUnixSeconds);
        return $"{pushDeviceTokenId:N}.{expiresAtUnixSeconds}.{Base64UrlEncode(mac)}";
    }

    private bool Validate(string domain, string token, Guid deliveryId, out Guid pushDeviceTokenId)
    {
        pushDeviceTokenId = Guid.Empty;

        var parts = token.Split('.', 3);
        if (parts.Length != 3)
            return false;

        if (!Guid.TryParseExact(parts[0], "N", out var claimedTokenId))
            return false;

        if (!long.TryParse(parts[1], out var expiresAtUnixSeconds))
            return false;

        byte[] presentedMac;
        try
        {
            presentedMac = Base64UrlDecode(parts[2]);
        }
        catch (FormatException)
        {
            return false;
        }

        // Expiry checked before the constant-time compare — cheap rejection of a stale token.
        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(expiresAtUnixSeconds).UtcDateTime;
        if (expiresAt < DateTime.UtcNow)
            return false;

        var expectedMac = Compute(domain, deliveryId, claimedTokenId, expiresAtUnixSeconds);

        // Constant-time — there is no ciphertext to decrypt here, only a MAC to compare, so this
        // is the whole check (unlike AesEncryptionService, which authenticates as a side effect
        // of successfully decrypting).
        if (presentedMac.Length != expectedMac.Length || !CryptographicOperations.FixedTimeEquals(presentedMac, expectedMac))
            return false;

        pushDeviceTokenId = claimedTokenId;
        return true;
    }

    private byte[] Compute(string domain, Guid deliveryId, Guid pushDeviceTokenId, long expiresAtUnixSeconds)
    {
        var payload = Encoding.UTF8.GetBytes($"{domain}|{deliveryId:N}|{pushDeviceTokenId:N}|{expiresAtUnixSeconds}");
        return HMACSHA256.HashData(_key, payload);
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        return Convert.FromBase64String(padded);
    }
}
