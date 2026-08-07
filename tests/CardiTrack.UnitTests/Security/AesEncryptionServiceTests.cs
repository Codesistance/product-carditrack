using System.Security.Cryptography;
using System.Text;
using CardiTrack.Infrastructure.Security;

namespace CardiTrack.UnitTests.Security;

public class AesEncryptionServiceTests
{
    private readonly AesEncryptionService _sut = new(AesEncryptionService.GenerateKey());

    // ── Key validation ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Ctor_RejectsMissingKey(string? key)
    {
        Assert.Throws<ArgumentException>(() => new AesEncryptionService(key!));
    }

    [Theory]
    [InlineData(16)]
    [InlineData(31)]
    [InlineData(33)]
    public void Ctor_RejectsKeysThatAreNot256Bits(int keyBytes)
    {
        var shortKey = Convert.ToBase64String(new byte[keyBytes]);

        Assert.Throws<ArgumentException>(() => new AesEncryptionService(shortKey));
    }

    [Theory]
    [InlineData("GENERATE_32_BYTE_ENCRYPTION_KEY_HERE_REPLACE_WITH_SECURE_KEY")] // historical appsettings stub
    [InlineData("REPLACE_ME")]                                                   // Terraform Secret Manager placeholder
    [InlineData("replace_me")]
    [InlineData("  REPLACE_ME  ")]
    public void Ctor_RejectsPlaceholderKeysAsUnset(string key)
    {
        var ex = Assert.Throws<ArgumentException>(() => new AesEncryptionService(key));

        Assert.Contains("Encryption:Key", ex.Message);
        Assert.Contains("is not set", ex.Message);
    }

    [Theory]
    [InlineData("not base64 at all!")]
    [InlineData("****")]
    public void Ctor_RejectsNonBase64KeysWithAnActionableMessage(string key)
    {
        // A raw FormatException from Convert.FromBase64String names neither the setting nor the fix.
        var ex = Assert.Throws<ArgumentException>(() => new AesEncryptionService(key));

        Assert.Contains("Encryption:Key", ex.Message);
        Assert.Contains("base64", ex.Message);
    }

    [Fact]
    public void Ctor_AcceptsKeyWithSurroundingWhitespace()
    {
        // Secret Manager values are frequently seeded with a trailing newline.
        var padded = $"\n{AesEncryptionService.GenerateKey()}\n";

        var service = new AesEncryptionService(padded);

        Assert.Equal("payload", service.Decrypt(service.Encrypt("payload")));
    }

    [Fact]
    public void GenerateKey_ProducesDistinct256BitKeys()
    {
        var first = AesEncryptionService.GenerateKey();
        var second = AesEncryptionService.GenerateKey();

        Assert.Equal(32, Convert.FromBase64String(first).Length);
        Assert.NotEqual(first, second);
    }

    // ── String round-trips ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("access-token-12345")]
    [InlineData("Pacemaker fitted 2019; allergic to penicillin")]
    [InlineData("émojis and ünïcode — ❤️🫀")]
    public void EncryptDecrypt_RoundTripsText(string plainText)
    {
        var cipherText = _sut.Encrypt(plainText);

        Assert.NotEqual(plainText, cipherText);
        Assert.Equal(plainText, _sut.Decrypt(cipherText));
    }

    [Fact]
    public void Encrypt_PassesThroughNullAndEmpty()
    {
        Assert.Null(_sut.Encrypt(null!));
        Assert.Equal(string.Empty, _sut.Encrypt(string.Empty));
    }

    [Fact]
    public void Decrypt_PassesThroughNullAndEmpty()
    {
        Assert.Equal(string.Empty, _sut.Decrypt(null!));
        Assert.Equal(string.Empty, _sut.Decrypt(string.Empty));
    }

    [Fact]
    public void Encrypt_ProducesDifferentCipherTextPerCall()
    {
        var first = _sut.Encrypt("same input");
        var second = _sut.Encrypt("same input");

        Assert.NotEqual(first, second);
    }

    // ── Byte round-trips and format ─────────────────────────────────────────────

    [Fact]
    public void EncryptBytes_PrependsNonceAndTag()
    {
        var plainBytes = Encoding.UTF8.GetBytes("payload");

        var cipherBytes = _sut.EncryptBytes(plainBytes);

        Assert.Equal(12 + 16 + plainBytes.Length, cipherBytes.Length);
        Assert.Equal(plainBytes, _sut.DecryptBytes(cipherBytes));
    }

    [Fact]
    public void EncryptBytes_PassesThroughNullAndEmpty()
    {
        Assert.Empty(_sut.EncryptBytes(null!));
        Assert.Empty(_sut.EncryptBytes([]));
        Assert.Empty(_sut.DecryptBytes(null!));
        Assert.Empty(_sut.DecryptBytes([]));
    }

    // ── Tamper and key checks ───────────────────────────────────────────────────

    [Fact]
    public void DecryptBytes_RejectsTamperedCipherText()
    {
        var cipherBytes = _sut.EncryptBytes(Encoding.UTF8.GetBytes("payload"));
        cipherBytes[^1] ^= 0xFF;

        Assert.ThrowsAny<CryptographicException>(() => _sut.DecryptBytes(cipherBytes));
    }

    [Fact]
    public void DecryptBytes_RejectsTamperedTag()
    {
        var cipherBytes = _sut.EncryptBytes(Encoding.UTF8.GetBytes("payload"));
        cipherBytes[12] ^= 0xFF;

        Assert.ThrowsAny<CryptographicException>(() => _sut.DecryptBytes(cipherBytes));
    }

    [Fact]
    public void DecryptBytes_RejectsInputShorterThanNoncePlusTag()
    {
        Assert.Throws<ArgumentException>(() => _sut.DecryptBytes(new byte[27]));
    }

    [Fact]
    public void Decrypt_FailsWithDifferentKey()
    {
        var cipherText = _sut.Encrypt("secret");
        var otherService = new AesEncryptionService(AesEncryptionService.GenerateKey());

        // Was a bare CryptographicException. The envelope now names both key ids, so a
        // misconfigured deployment is distinguishable from corrupted data.
        var ex = Assert.Throws<InvalidOperationException>(() => otherService.Decrypt(cipherText));
        Assert.Contains(_sut.KeyId, ex.Message);
        Assert.Contains(otherService.KeyId, ex.Message);
    }

    // ── Key-id envelope ─────────────────────────────────────────────────────────
    //
    // The envelope is what makes key rotation and crypto-shredding possible later: without a
    // recorded key id nothing can tell which key a stored row was written under.

    [Fact]
    public void Encrypt_StampsTheEnvelopeWithVersionAndKeyId()
    {
        var cipherText = _sut.Encrypt("secret");

        var parts = cipherText.Split(':');
        Assert.Equal(3, parts.Length);
        Assert.Equal("v1", parts[0]);
        Assert.Equal(_sut.KeyId, parts[1]);
        Assert.NotEmpty(Convert.FromBase64String(parts[2]));
    }

    [Fact]
    public void KeyId_IsStableForAKey_AndDiffersBetweenKeys()
    {
        var key = AesEncryptionService.GenerateKey();

        Assert.Equal(new AesEncryptionService(key).KeyId, new AesEncryptionService(key).KeyId);
        Assert.NotEqual(_sut.KeyId, new AesEncryptionService(key).KeyId);
    }

    [Fact]
    public void KeyId_DoesNotRevealTheKey()
    {
        var key = AesEncryptionService.GenerateKey();

        var service = new AesEncryptionService(key);

        Assert.DoesNotContain(service.KeyId, key, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Decrypt_ReadsCipherTextWrittenBeforeEnvelopesExisted()
    {
        // Exactly the old on-disk format: bare base64 of nonce|tag|ciphertext, no prefix.
        // OAuth refresh tokens already sit in the database in this shape, so losing the ability
        // to read them would break device sync for every connected wearer.
        var legacy = Convert.ToBase64String(_sut.EncryptBytes(Encoding.UTF8.GetBytes("legacy secret")));

        Assert.Equal("legacy secret", _sut.Decrypt(legacy));
    }

    [Theory]
    [InlineData("v2")]
    [InlineData("vX")]
    public void Decrypt_TreatsAnUnknownEnvelopeVersionAsLegacyRatherThanGuessing(string version)
    {
        var payload = Convert.ToBase64String(_sut.EncryptBytes(Encoding.UTF8.GetBytes("x")));

        // An unrecognised prefix is not silently parsed as if it were ours; the whole string is
        // treated as the payload, which then fails to decode rather than decrypting wrongly.
        Assert.ThrowsAny<Exception>(() => _sut.Decrypt($"{version}:{_sut.KeyId}:{payload}"));
    }
}
