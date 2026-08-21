using System.Diagnostics;
using System.Globalization;
using CardiTrack.Mobile.Core.Auth;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CardiTrack.Mobile.Services;

/// <summary>
/// Persists tokens in platform secure storage (Keychain/Keystore). On unpackaged Windows
/// SecureStorage can throw; we fall back to Preferences there — a dev-only convenience,
/// Windows is not a shipping target for auth.
/// </summary>
public sealed class SecureTokenStore : ITokenStore
{
    private const string AccessTokenKey = "auth.access_token";
    private const string RefreshTokenKey = "auth.refresh_token";
    private const string IdTokenKey = "auth.id_token";
    private const string ExpiresAtKey = "auth.expires_at";

    // Keystore/Keychain calls are documented to occasionally hang rather than throw — never
    // returning, never faulting — most often after a reinstall leaves a stale signing-key
    // alias. Sign-in awaits this store directly, so a real hang here reads as the whole app
    // being stuck on the sign-in screen. Bounding the wait converts that into the existing
    // caught-exception/fallback path instead of blocking forever.
    private static readonly TimeSpan SecureStorageTimeout = TimeSpan.FromSeconds(5);

    // Standard System.Diagnostics tracing rather than a Datadog-specific call, so this stays
    // engine-agnostic: it costs nothing while no listener is attached, and lights up for free
    // once one is (see MobileApm) — no rework needed here when that happens.
    private static readonly ActivitySource ActivitySource = new("CardiTrack.Mobile.Auth.SecureTokenStore");

    // What actually happens below on a SecureStorage failure — only true on Windows, where
    // FallbackGet/FallbackSave persist to Preferences; on Android/iOS they're no-ops, and a log
    // line claiming otherwise would misdiagnose exactly the platforms this timeout exists for.
#if WINDOWS
    private const string FallbackDescription = "using Preferences fallback";
#else
    private const string FallbackDescription = "no fallback available on this platform";
#endif

    private readonly ILogger<SecureTokenStore> _logger;

    public SecureTokenStore(ILogger<SecureTokenStore>? logger = null)
    {
        _logger = logger ?? NullLogger<SecureTokenStore>.Instance;
    }

    public async Task<AuthTokens?> GetAsync()
    {
        using var activity = ActivitySource.StartActivity("SecureTokenStore.Get");

        try
        {
            var accessToken = await GetWithTimeoutAsync(AccessTokenKey);
            if (string.IsNullOrEmpty(accessToken))
            {
                activity?.SetTag("carditrack.secure_storage.result", "empty");
                return null;
            }

            var refreshToken = await GetWithTimeoutAsync(RefreshTokenKey);
            var idToken = await GetWithTimeoutAsync(IdTokenKey);
            var expiresAtRaw = await GetWithTimeoutAsync(ExpiresAtKey);
            var expiresAt = long.TryParse(expiresAtRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix)
                ? DateTimeOffset.FromUnixTimeSeconds(unix)
                : DateTimeOffset.MinValue;

            activity?.SetTag("carditrack.secure_storage.result", "hit");
            _logger.LogDebug("SecureTokenStore: read succeeded, expires {ExpiresAt}", expiresAt);
            return new AuthTokens(accessToken, refreshToken, idToken, expiresAt);
        }
        catch (TimeoutException ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.SetTag("carditrack.secure_storage.result", "timeout");
            // Louder than the generic fallback below: this is the hang this timeout guard
            // exists to catch, and unlike a normal SecureStorage miss it's worth someone
            // noticing rather than quietly degrading.
            _logger.LogError(ex, "SecureTokenStore: read timed out; {Fallback}", FallbackDescription);
            return FallbackGet();
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.SetTag("carditrack.secure_storage.result", "error");
            _logger.LogWarning(ex, "SecureTokenStore: read failed; {Fallback}", FallbackDescription);
            return FallbackGet();
        }
    }

    public async Task SaveAsync(AuthTokens tokens)
    {
        using var activity = ActivitySource.StartActivity("SecureTokenStore.Save");

        try
        {
            await SetWithTimeoutAsync(AccessTokenKey, tokens.AccessToken);
            await SetWithTimeoutAsync(RefreshTokenKey, tokens.RefreshToken ?? string.Empty);
            await SetWithTimeoutAsync(IdTokenKey, tokens.IdToken ?? string.Empty);
            await SetWithTimeoutAsync(ExpiresAtKey,
                tokens.ExpiresAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));

            activity?.SetTag("carditrack.secure_storage.result", "ok");
        }
        catch (TimeoutException ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.SetTag("carditrack.secure_storage.result", "timeout");
            _logger.LogError(ex, "SecureTokenStore: write timed out; {Fallback}", FallbackDescription);
            FallbackSave(tokens);
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.SetTag("carditrack.secure_storage.result", "error");
            _logger.LogWarning(ex, "SecureTokenStore: write failed; {Fallback}", FallbackDescription);
            FallbackSave(tokens);
        }
    }

    /// <summary>
    /// Runs the SecureStorage call on a thread-pool thread rather than awaiting it directly.
    /// On Android, SecureStorage.SetAsync/GetAsync can do first-use Keystore/EncryptedSharedPreferences
    /// work synchronously on the calling thread despite the async signature — awaited straight
    /// from the UI thread (as sign-in does), that blocks the UI thread itself and Android reports
    /// an ANR, which a plain <see cref="Task.WaitAsync(TimeSpan)"/> around the returned Task
    /// cannot help with: the block happens before that Task exists to time out. Task.Run moves
    /// the call (sync portion included) off the UI thread first, so the timeout below always has
    /// something to race against and the UI thread never blocks either way.
    /// </summary>
    private static Task<string?> GetWithTimeoutAsync(string key) =>
        Task.Run(() => SecureStorage.Default.GetAsync(key)).WaitAsync(SecureStorageTimeout);

    private static Task SetWithTimeoutAsync(string key, string value) =>
        Task.Run(() => SecureStorage.Default.SetAsync(key, value)).WaitAsync(SecureStorageTimeout);

    public Task ClearAsync()
    {
        try
        {
            SecureStorage.Default.Remove(AccessTokenKey);
            SecureStorage.Default.Remove(RefreshTokenKey);
            SecureStorage.Default.Remove(IdTokenKey);
            SecureStorage.Default.Remove(ExpiresAtKey);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SecureStorage clear failed; clearing Preferences fallback only");
        }
        FallbackClear();
        return Task.CompletedTask;
    }

#if WINDOWS
    private static AuthTokens? FallbackGet()
    {
        var accessToken = Preferences.Default.Get(AccessTokenKey, string.Empty);
        if (string.IsNullOrEmpty(accessToken))
            return null;
        var refreshToken = Preferences.Default.Get(RefreshTokenKey, string.Empty);
        var idToken = Preferences.Default.Get(IdTokenKey, string.Empty);
        var unix = Preferences.Default.Get(ExpiresAtKey, 0L);
        return new AuthTokens(
            accessToken,
            string.IsNullOrEmpty(refreshToken) ? null : refreshToken,
            string.IsNullOrEmpty(idToken) ? null : idToken,
            DateTimeOffset.FromUnixTimeSeconds(unix));
    }

    private static void FallbackSave(AuthTokens tokens)
    {
        System.Diagnostics.Debug.WriteLine("SecureStorage unavailable — falling back to Preferences (dev only).");
        Preferences.Default.Set(AccessTokenKey, tokens.AccessToken);
        Preferences.Default.Set(RefreshTokenKey, tokens.RefreshToken ?? string.Empty);
        Preferences.Default.Set(IdTokenKey, tokens.IdToken ?? string.Empty);
        Preferences.Default.Set(ExpiresAtKey, tokens.ExpiresAt.ToUnixTimeSeconds());
    }

    private static void FallbackClear()
    {
        Preferences.Default.Remove(AccessTokenKey);
        Preferences.Default.Remove(RefreshTokenKey);
        Preferences.Default.Remove(IdTokenKey);
        Preferences.Default.Remove(ExpiresAtKey);
    }
#else
    private static AuthTokens? FallbackGet() => null;
    private static void FallbackSave(AuthTokens tokens) { }
    private static void FallbackClear() { }
#endif
}
