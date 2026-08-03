using System.Globalization;
using CardiTrack.Mobile.Core.Auth;

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

    public async Task<AuthTokens?> GetAsync()
    {
        try
        {
            var accessToken = await SecureStorage.Default.GetAsync(AccessTokenKey);
            if (string.IsNullOrEmpty(accessToken))
                return null;

            var refreshToken = await SecureStorage.Default.GetAsync(RefreshTokenKey);
            var idToken = await SecureStorage.Default.GetAsync(IdTokenKey);
            var expiresAtRaw = await SecureStorage.Default.GetAsync(ExpiresAtKey);
            var expiresAt = long.TryParse(expiresAtRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix)
                ? DateTimeOffset.FromUnixTimeSeconds(unix)
                : DateTimeOffset.MinValue;

            return new AuthTokens(accessToken, refreshToken, idToken, expiresAt);
        }
        catch (Exception)
        {
            return FallbackGet();
        }
    }

    public async Task SaveAsync(AuthTokens tokens)
    {
        try
        {
            await SecureStorage.Default.SetAsync(AccessTokenKey, tokens.AccessToken);
            await SecureStorage.Default.SetAsync(RefreshTokenKey, tokens.RefreshToken ?? string.Empty);
            await SecureStorage.Default.SetAsync(IdTokenKey, tokens.IdToken ?? string.Empty);
            await SecureStorage.Default.SetAsync(ExpiresAtKey,
                tokens.ExpiresAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception)
        {
            FallbackSave(tokens);
        }
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
        catch (Exception)
        {
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
