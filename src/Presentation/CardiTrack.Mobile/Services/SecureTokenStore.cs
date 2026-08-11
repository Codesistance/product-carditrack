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

    // Standard System.Diagnostics tracing rather than a Datadog-specific call: RUM isn't
    // provisioned on mobile yet (see MobileApm), so this is a no-op today, but it lights up
    // for free the moment any OTel-compatible listener (Datadog's or otherwise) is attached —
    // no rework needed here when that happens.
    private static readonly ActivitySource ActivitySource = new("CardiTrack.Mobile.Auth.SecureTokenStore");

    private readonly ILogger<SecureTokenStore> _logger;

    public SecureTokenStore(ILogger<SecureTokenStore>? logger = null)
    {
        _logger = logger ?? NullLogger<SecureTokenStore>.Instance;
    }

    public async Task<AuthTokens?> GetAsync()
    {
        using var activity = ActivitySource.StartActivity("SecureTokenStore.Get");
        _logger.LogDebug("SecureTokenStore: reading tokens from platform secure storage");

        try
        {
            var accessToken = await GetWithTimeoutAsync(AccessTokenKey);
            if (string.IsNullOrEmpty(accessToken))
            {
                activity?.SetTag("carditrack.secure_storage.result", "empty");
                _logger.LogDebug("SecureTokenStore: no stored access token");
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
            _logger.LogError(ex, "SecureTokenStore: read timed out; using Preferences fallback");
            return FallbackGet();
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.SetTag("carditrack.secure_storage.result", "error");
            _logger.LogWarning(ex, "SecureTokenStore: read failed; using Preferences fallback");
            return FallbackGet();
        }
    }

    public async Task SaveAsync(AuthTokens tokens)
    {
        using var activity = ActivitySource.StartActivity("SecureTokenStore.Save");
        _logger.LogDebug("SecureTokenStore: saving tokens to platform secure storage");

        try
        {
            await SetWithTimeoutAsync(AccessTokenKey, tokens.AccessToken);
            await SetWithTimeoutAsync(RefreshTokenKey, tokens.RefreshToken ?? string.Empty);
            await SetWithTimeoutAsync(IdTokenKey, tokens.IdToken ?? string.Empty);
            await SetWithTimeoutAsync(ExpiresAtKey,
                tokens.ExpiresAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));

            activity?.SetTag("carditrack.secure_storage.result", "ok");
            _logger.LogDebug("SecureTokenStore: save succeeded");
        }
        catch (TimeoutException ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.SetTag("carditrack.secure_storage.result", "timeout");
            _logger.LogError(ex, "SecureTokenStore: write timed out; using Preferences fallback");
            FallbackSave(tokens);
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.SetTag("carditrack.secure_storage.result", "error");
            _logger.LogWarning(ex, "SecureTokenStore: write failed; using Preferences fallback");
            FallbackSave(tokens);
        }
    }

    /// <summary>
    /// Abandons (doesn't cancel — SecureStorage exposes no way to) the native call on timeout
    /// so the caller can proceed; a late completion is still safely picked up by the app's
    /// unobserved-task-exception handler if it ever faults.
    /// </summary>
    private static async Task<string?> GetWithTimeoutAsync(string key)
    {
        var task = SecureStorage.Default.GetAsync(key);
        var finished = await Task.WhenAny(task, Task.Delay(SecureStorageTimeout));
        if (finished != task)
            throw new TimeoutException($"SecureStorage read of '{key}' timed out after {SecureStorageTimeout}.");
        return await task;
    }

    private static async Task SetWithTimeoutAsync(string key, string value)
    {
        var task = SecureStorage.Default.SetAsync(key, value);
        var finished = await Task.WhenAny(task, Task.Delay(SecureStorageTimeout));
        if (finished != task)
            throw new TimeoutException($"SecureStorage write of '{key}' timed out after {SecureStorageTimeout}.");
        await task;
    }

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
