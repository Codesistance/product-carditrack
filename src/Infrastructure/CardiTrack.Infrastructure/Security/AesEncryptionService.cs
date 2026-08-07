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
    private const int NonceSize = 12; // 96 bits for GCM
    private const int TagSize = 16;   // 128 bits authentication tag
    private const int KeySize = 32;   // 256 bits

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
    }

    public string Encrypt(string plainText)
    {
        if (string.IsNullOrEmpty(plainText))
            return plainText;

        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var cipherBytes = EncryptBytes(plainBytes);
        return Convert.ToBase64String(cipherBytes);
    }

    public string Decrypt(string cipherText)
    {
        if (string.IsNullOrEmpty(cipherText))
            return cipherText ?? string.Empty;

        var cipherBytes = Convert.FromBase64String(cipherText);
        var plainBytes = DecryptBytes(cipherBytes);
        return Encoding.UTF8.GetString(plainBytes);
    }

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
