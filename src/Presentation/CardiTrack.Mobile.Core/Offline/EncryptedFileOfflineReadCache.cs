using System.Security.Cryptography;
using System.Text;
using CardiTrack.Mobile.Core.Onboarding;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CardiTrack.Mobile.Core.Offline;

/// <summary>
/// AES-256-GCM encrypted files in a caller-nominated directory, keyed by a DEK held in
/// <see cref="ISecureKeyValueStore"/> (Keychain / Keystore). The same fail-closed rule as
/// <c>CardiMemberDraftStore</c>: if the secure store is unavailable, nothing is written.
/// </summary>
/// <remarks>
/// This is the last-known-good snapshot for offline reads — not the R4 SQLite 7-day history
/// and write queue. One encrypted blob per GET path, dropped after
/// <see cref="IOfflineReadCache.Lifetime"/>.
/// </remarks>
public sealed class EncryptedFileOfflineReadCache : IOfflineReadCache
{
    private const string DekKey = "offline-cache.dek";
    private const int DekSize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private static readonly byte[] Magic = "CTO1"u8.ToArray();

    private readonly string _directory;
    private readonly ISecureKeyValueStore _secure;
    private readonly TimeProvider _clock;
    private readonly ILogger<EncryptedFileOfflineReadCache> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private byte[]? _dek;
    private int _epoch;

    public EncryptedFileOfflineReadCache(
        string directory,
        ISecureKeyValueStore secure,
        TimeProvider? clock = null,
        ILogger<EncryptedFileOfflineReadCache>? logger = null)
    {
        _directory = directory;
        _secure = secure;
        _clock = clock ?? TimeProvider.System;
        _logger = logger ?? NullLogger<EncryptedFileOfflineReadCache>.Instance;
    }

    public async Task SaveAsync(string key, string payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(payload);

        var (dek, epoch) = await GetDekAsync(ct);
        if (dek is null)
            return;

        var plaintext = Encoding.UTF8.GetBytes(payload);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        using (var aes = new AesGcm(dek, TagSize))
            aes.Encrypt(nonce, plaintext, ciphertext, tag);

        var cachedAt = _clock.GetUtcNow();
        var file = new byte[Magic.Length + sizeof(long) + NonceSize + TagSize + ciphertext.Length];
        var span = file.AsSpan();
        Magic.CopyTo(span);
        BitConverter.TryWriteBytes(span.Slice(Magic.Length, sizeof(long)), cachedAt.UtcTicks);
        nonce.CopyTo(span.Slice(Magic.Length + sizeof(long), NonceSize));
        tag.CopyTo(span.Slice(Magic.Length + sizeof(long) + NonceSize, TagSize));
        ciphertext.CopyTo(span.Slice(Magic.Length + sizeof(long) + NonceSize + TagSize));

        await _gate.WaitAsync(ct);
        try
        {
            if (epoch != _epoch)
                return;

            Directory.CreateDirectory(_directory);
            await File.WriteAllBytesAsync(PathFor(key), file, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Offline cache write failed for {CacheKey}", key);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<OfflineCacheEntry?> TryGetAsync(string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var (dek, epoch) = await GetDekAsync(ct);
        if (dek is null)
            return null;

        await _gate.WaitAsync(ct);
        byte[] file;
        try
        {
            if (epoch != _epoch)
                return null;

            var path = PathFor(key);
            if (!File.Exists(path))
                return null;
            file = await File.ReadAllBytesAsync(path, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Offline cache read failed for {CacheKey}", key);
            return null;
        }
        finally
        {
            _gate.Release();
        }

        if (!TryDecrypt(dek, file, out var payload, out var cachedAt))
        {
            await RemoveAsync(key, epoch, ct);
            return null;
        }

        if (_clock.GetUtcNow() - cachedAt > IOfflineReadCache.Lifetime)
        {
            await RemoveAsync(key, epoch, ct);
            return null;
        }

        // Sign-out may have cleared the cache while we were decrypting — do not hand the
        // previous caregiver's snapshot back after the wipe has finished.
        if (epoch != _epoch)
            return null;

        return new OfflineCacheEntry(payload, cachedAt);
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _epoch++;
            try
            {
                if (Directory.Exists(_directory))
                    Directory.Delete(_directory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Offline cache directory delete failed");
            }

            _dek = null;
            // Rotate the DEK under the same gate as the epoch bump so a concurrent save
            // cannot reload the old key after this increment and write the previous
            // snapshot into the new session's directory.
            try
            {
                _secure.Remove(DekKey);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Offline cache DEK remove failed");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<(byte[]? Dek, int Epoch)> GetDekAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return (await GetOrCreateDekUnlockedAsync(), _epoch);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Caller already holds <see cref="_gate"/>.</summary>
    private async Task<byte[]?> GetOrCreateDekUnlockedAsync()
    {
        if (_dek is not null)
            return _dek;

        try
        {
            var existing = await _secure.GetAsync(DekKey);
            if (!string.IsNullOrEmpty(existing))
            {
                try
                {
                    var decoded = Convert.FromBase64String(existing);
                    if (decoded.Length == DekSize)
                        return _dek = decoded;
                }
                catch (FormatException)
                {
                    // Corrupt DEK: rotate rather than disabling the cache until someone
                    // clears Keystore by hand. Existing blobs become unreadable, which is
                    // the same outcome as a missing key.
                }
            }

            var dek = RandomNumberGenerator.GetBytes(DekSize);
            await _secure.SetAsync(DekKey, Convert.ToBase64String(dek));
            return _dek = dek;
        }
        catch (Exception ex)
        {
            // Fail closed: never write wearer data in the clear because Keystore is unhappy.
            _logger.LogWarning(ex, "Offline cache DEK unavailable; skipping cache I/O");
            return null;
        }
    }

    /// <summary>
    /// Best-effort: a delete that fails leaves an entry that is merely stale, not wrong, and the
    /// callers are all on paths where throwing would be the worse outcome — a failed read, or a
    /// caller tidying up after itself.
    /// </summary>
    public Task RemoveAsync(string key, CancellationToken ct = default) =>
        RemoveAsync(key, expectedEpoch: null, ct);

    private async Task RemoveAsync(string key, int? expectedEpoch, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        await _gate.WaitAsync(ct);
        try
        {
            // A decrypt-fail/expiry tidy that started before Clear must not delete the
            // next session's file at the same hashed path.
            if (expectedEpoch is { } epoch && epoch != _epoch)
                return;

            var path = PathFor(key);
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Offline cache entry delete failed for {CacheKey}", key);
        }
        finally
        {
            _gate.Release();
        }
    }

    private string PathFor(string key)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        return Path.Combine(_directory, $"{hash}.bin");
    }

    private static bool TryDecrypt(byte[] dek, byte[] file, out string payload, out DateTimeOffset cachedAt)
    {
        payload = string.Empty;
        cachedAt = default;

        var header = Magic.Length + sizeof(long) + NonceSize + TagSize;
        if (file.Length < header)
            return false;
        if (!file.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            return false;

        var ticks = BitConverter.ToInt64(file, Magic.Length);
        try
        {
            cachedAt = new DateTimeOffset(ticks, TimeSpan.Zero);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }

        var nonce = file.AsSpan(Magic.Length + sizeof(long), NonceSize);
        var tag = file.AsSpan(Magic.Length + sizeof(long) + NonceSize, TagSize);
        var ciphertext = file.AsSpan(header);
        var plaintext = new byte[ciphertext.Length];

        try
        {
            using var aes = new AesGcm(dek, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
        }
        catch (CryptographicException)
        {
            return false;
        }

        payload = Encoding.UTF8.GetString(plaintext);
        return true;
    }
}
