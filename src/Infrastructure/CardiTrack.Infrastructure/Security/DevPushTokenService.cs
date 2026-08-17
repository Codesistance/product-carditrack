using System.Security.Cryptography;
using System.Text;
using CardiTrack.Application.Interfaces.Security;
using CardiTrack.Domain.Enums;
using CardiTrack.Shared;

namespace CardiTrack.Infrastructure.Security;

/// <summary>
/// The HMAC-SHA256 token computation behind the dev-only test-push endpoint
/// (notification_engine.md §13). Deliberately the same construction as
/// <see cref="AckTokenService"/> — expiry checked before a constant-time MAC compare, base64url
/// wire format, domain-separated payload — so there is one token shape to reason about in this
/// codebase rather than two.
/// </summary>
/// <remarks>
/// Token format is <c>{expiresAtUnixSeconds}.{base64url(hmac)}</c>. Nothing else travels in the
/// clear because nothing else needs to: unlike an ack token, which must tell the server which
/// device it came from, the target of a test push is already in the request body. The MAC covers
/// that body — <c>userId</c>, <c>category</c>, <c>severity</c> — so a token captured off one
/// request cannot be replayed against a different user or upgraded to a category that overrides
/// quiet hours.
/// <para>
/// <see cref="MaxLifetime"/> caps how far ahead an expiry may sit. The holder of the key could
/// always mint another token, so this is not a limit on them; it bounds the window in which a
/// token that leaks — out of a shell history, a scratch file, a screen share — is still good.
/// </para>
/// </remarks>
public class DevPushTokenService : IDevPushTokenService
{
    /// <summary>Longest expiry this service will issue or accept, measured from now.</summary>
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromMinutes(10);

    private const string Domain = "devpush";
    private const int KeySize = 32;

    private static readonly HashSet<string> Placeholders = new(StringComparer.OrdinalIgnoreCase)
    {
        "REPLACE_ME"
    };

    private readonly byte[] _key;
    private readonly TimeProvider _timeProvider;

    public DevPushTokenService(string base64Key, TimeProvider? timeProvider = null)
    {
        var key = base64Key?.Trim();

        if (string.IsNullOrEmpty(key) || Placeholders.Contains(key))
            throw new ArgumentException(
                $"'{ConfigurationKeys.Dev.PushTokenKey}' is not set. Provide a base64-encoded " +
                $"256-bit key via environment variable " +
                $"'{ConfigurationLoader.ToEnvVarKey(ConfigurationKeys.Dev.PushTokenKey)}' or user-secrets.",
                nameof(base64Key));

        var buffer = new byte[KeySize];
        if (!Convert.TryFromBase64String(key, buffer, out var written) || written != KeySize)
            throw new ArgumentException(
                $"'{ConfigurationKeys.Dev.PushTokenKey}' must be a base64-encoded 256-bit " +
                $"({KeySize}-byte) key. The configured value is not valid base64 or does not decode to that length.",
                nameof(base64Key));

        _key = buffer;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Builds the service if <paramref name="base64Key"/> is usable, returning false rather than
    /// throwing if it is not.
    /// </summary>
    /// <remarks>
    /// The gate needs to distinguish "no key, stay off" from "a key that cannot work" *before*
    /// deciding whether to route the endpoint. Presence alone is not enough: a placeholder or a
    /// mistyped key is non-empty, so a presence check would route the controller and then fail
    /// only when someone called it. Disabling on an unusable key is the honest outcome — the
    /// endpoint cannot authorize anything without a valid key, so it has nothing to offer.
    /// </remarks>
    public static bool TryCreate(string? base64Key, TimeProvider? timeProvider, out DevPushTokenService? service)
    {
        try
        {
            service = new DevPushTokenService(base64Key!, timeProvider);
            return true;
        }
        catch (ArgumentException)
        {
            service = null;
            return false;
        }
    }

    public string Issue(Guid userId, DeliveryCategory category, AlertSeverity? severity, DateTime expiresAt)
    {
        var expiresAtUnixSeconds = new DateTimeOffset(DateTime.SpecifyKind(expiresAt, DateTimeKind.Utc)).ToUnixTimeSeconds();

        if (expiresAt > _timeProvider.GetUtcNow().UtcDateTime + MaxLifetime)
            throw new ArgumentOutOfRangeException(
                nameof(expiresAt),
                $"A dev push token may not outlive {MaxLifetime.TotalMinutes:0} minutes from now.");

        var mac = Compute(userId, category, severity, expiresAtUnixSeconds);
        return $"{expiresAtUnixSeconds}.{Base64UrlEncode(mac)}";
    }

    public bool Validate(string token, Guid userId, DeliveryCategory category, AlertSeverity? severity)
    {
        if (string.IsNullOrEmpty(token))
            return false;

        var parts = token.Split('.');
        if (parts.Length != 2)
            return false;

        if (!long.TryParse(parts[0], out var expiresAtUnixSeconds))
            return false;

        byte[] presentedMac;
        try
        {
            presentedMac = Base64UrlDecode(parts[1]);
        }
        catch (FormatException)
        {
            return false;
        }

        // Both bounds checked before the constant-time compare, matching AckTokenService's cheap
        // rejection of a stale token. The upper bound matters as much as the lower one here: an
        // expiry far in the future is the shape a leaked token gets reused with.
        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(expiresAtUnixSeconds).UtcDateTime;
        if (expiresAt < utcNow || expiresAt > utcNow + MaxLifetime)
            return false;

        var expectedMac = Compute(userId, category, severity, expiresAtUnixSeconds);

        return presentedMac.Length == expectedMac.Length
            && CryptographicOperations.FixedTimeEquals(presentedMac, expectedMac);
    }

    private byte[] Compute(Guid userId, DeliveryCategory category, AlertSeverity? severity, long expiresAtUnixSeconds)
    {
        // "-" rather than "" for an absent severity: an empty field would let (Health, null) and
        // (Health, <a severity whose name is empty>) collide, and more usefully it keeps the
        // separator count fixed so no two distinct tuples can render to the same string.
        var severityPart = severity?.ToString() ?? "-";
        var payload = Encoding.UTF8.GetBytes(
            $"{Domain}|{userId:N}|{category}|{severityPart}|{expiresAtUnixSeconds}");
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
