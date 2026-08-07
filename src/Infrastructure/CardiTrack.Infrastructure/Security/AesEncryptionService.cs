using System.Security.Cryptography;
using System.Text;
using CardiTrack.Application.Interfaces.Security;
using CardiTrack.Shared;

namespace CardiTrack.Infrastructure.Security;

/// <summary>
/// AES-256 encryption service for sensitive data (OAuth tokens, medical notes)
/// HIPAA Compliant - Uses AES-256-GCM for authenticated encryption
/// </summary>
public class AesEncryptionService : IEncryptionService
{
    private readonly byte[] _key;
    private readonly string _keyId;

    private const int NonceSize = 12; // 96 bits for GCM
    private const int TagSize = 16;   // 128 bits authentication tag
    private const int KeySize = 32;   // 256 bits

    // Ciphertext is stored as "{version}:{keyId}:{base64}". Recording the key id is what makes
    // rotation and crypto-shredding possible later: without it, nothing can tell which key any
    // given row was written under, so a key can never be retired and erasure-by-key-destruction
    // is not available. ':' is outside the base64 alphabet, so it cannot occur in the payload.
    private const string EnvelopeVersion = "v1";
    private const char Separator = ':';
    private const int KeyIdLength = 8;

    /// <summary>
    /// Seeded values that document the shape of the setting but are not keys — Terraform's
    /// Secret Manager placeholder and the historical appsettings.json stub. Treated as "unset"
    /// so an unprovisioned environment fails with a message that says what to do.
    /// </summary>
    private static readonly HashSet<string> Placeholders = new(StringComparer.OrdinalIgnoreCase)
    {
        "REPLACE_ME",
        "GENERATE_32_BYTE_ENCRYPTION_KEY_HERE_REPLACE_WITH_SECURE_KEY",
    };

    public AesEncryptionService(string base64Key)
    {
        var key = base64Key?.Trim();

        if (string.IsNullOrEmpty(key) || Placeholders.Contains(key))
            throw new ArgumentException(
                $"'{ConfigurationKeys.Encryption.Key}' is not set. Provide a base64-encoded 256-bit key " +
                $"via environment variable '{ConfigurationLoader.ToEnvVarKey(ConfigurationKeys.Encryption.Key)}' " +
                $"or configuration; generate one with {nameof(AesEncryptionService)}.{nameof(GenerateKey)}().",
                nameof(base64Key));

        // Validate without throwing FormatException — the raw exception says nothing about which
        // setting is wrong, and its message is unhelpful for a misconfigured deployment.
        var buffer = new byte[KeySize];
        if (!Convert.TryFromBase64String(key, buffer, out var written) || written != KeySize)
            throw new ArgumentException(
                $"'{ConfigurationKeys.Encryption.Key}' must be a base64-encoded 256-bit ({KeySize}-byte) key. " +
                "The configured value is not valid base64 or does not decode to that length.",
                nameof(base64Key));

        _key = buffer;
        _keyId = DeriveKeyId(buffer);
    }

    /// <summary>
    /// The key this service holds, as it appears in envelopes. Exposed for operational
    /// diagnostics — telling which key a deployment is configured with without revealing it.
    /// </summary>
    public string KeyId => _keyId;

    public string Encrypt(string plainText)
    {
        if (string.IsNullOrEmpty(plainText))
            return plainText;

        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var cipherBytes = EncryptBytes(plainBytes);
        return $"{EnvelopeVersion}{Separator}{_keyId}{Separator}{Convert.ToBase64String(cipherBytes)}";
    }

    public string Decrypt(string cipherText)
    {
        if (string.IsNullOrEmpty(cipherText))
            return cipherText ?? string.Empty;

        var (keyId, payload) = ParseEnvelope(cipherText);

        // A value written under a key we no longer hold cannot be read, and must say so plainly.
        // Without this the failure surfaces as a bare AuthenticationTagMismatchException, which
        // reads like corruption rather than "the wrong key is deployed".
        if (keyId is not null && !string.Equals(keyId, _keyId, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Ciphertext was written under encryption key '{keyId}', but this service holds " +
                $"key '{_keyId}'. Restore that key to read this value, or re-encrypt it under the " +
                "current key. Dropping a retired key is what makes its data unrecoverable by design.");

        var cipherBytes = Convert.FromBase64String(payload);
        var plainBytes = DecryptBytes(cipherBytes);
        return Encoding.UTF8.GetString(plainBytes);
    }

    /// <summary>
    /// Splits "<c>{version}:{keyId}:{base64}</c>". Returns a null key id for values written
    /// before envelopes existed — those are bare base64 and were encrypted under the key in use
    /// at the time, so they decrypt with the current key or not at all.
    /// </summary>
    private static (string? KeyId, string Payload) ParseEnvelope(string cipherText)
    {
        var parts = cipherText.Split(Separator);
        if (parts.Length != 3 || parts[0] != EnvelopeVersion)
            return (null, cipherText);

        return (parts[1], parts[2]);
    }

    /// <summary>
    /// A short, stable fingerprint of the key — not the key itself, and not reversible to it.
    /// Derived rather than configured so a rotated key cannot accidentally keep the old id and
    /// silently make old and new ciphertext indistinguishable.
    /// </summary>
    private static string DeriveKeyId(byte[] key) =>
        Convert.ToHexStringLower(SHA256.HashData(key))[..KeyIdLength];

    public byte[] EncryptBytes(byte[] plainBytes)
    {
        if (plainBytes == null || plainBytes.Length == 0)
            return plainBytes ?? Array.Empty<byte>();

        // Generate random nonce
        var nonce = new byte[NonceSize];
        RandomNumberGenerator.Fill(nonce);

        // Encrypt with AES-GCM
        var tag = new byte[TagSize];
        var cipherBytes = new byte[plainBytes.Length];

        using var aesGcm = new AesGcm(_key, TagSize);
        aesGcm.Encrypt(nonce, plainBytes, cipherBytes, tag);

        // Combine: nonce + tag + ciphertext
        var result = new byte[NonceSize + TagSize + cipherBytes.Length];
        Buffer.BlockCopy(nonce, 0, result, 0, NonceSize);
        Buffer.BlockCopy(tag, 0, result, NonceSize, TagSize);
        Buffer.BlockCopy(cipherBytes, 0, result, NonceSize + TagSize, cipherBytes.Length);

        return result;
    }

    public byte[] DecryptBytes(byte[] cipherBytes)
    {
        if (cipherBytes == null || cipherBytes.Length == 0)
            return cipherBytes ?? Array.Empty<byte>();

        if (cipherBytes.Length < NonceSize + TagSize)
            throw new ArgumentException("Invalid cipher text", nameof(cipherBytes));

        // Extract nonce, tag, and ciphertext
        var nonce = new byte[NonceSize];
        var tag = new byte[TagSize];
        var encrypted = new byte[cipherBytes.Length - NonceSize - TagSize];

        Buffer.BlockCopy(cipherBytes, 0, nonce, 0, NonceSize);
        Buffer.BlockCopy(cipherBytes, NonceSize, tag, 0, TagSize);
        Buffer.BlockCopy(cipherBytes, NonceSize + TagSize, encrypted, 0, encrypted.Length);

        // Decrypt with AES-GCM
        var plainBytes = new byte[encrypted.Length];

        using var aesGcm = new AesGcm(_key, TagSize);
        aesGcm.Decrypt(nonce, encrypted, tag, plainBytes);

        return plainBytes;
    }

    /// <summary>
    /// Generates a new 256-bit encryption key
    /// </summary>
    public static string GenerateKey()
    {
        var key = new byte[KeySize];
        RandomNumberGenerator.Fill(key);
        return Convert.ToBase64String(key);
    }
}
