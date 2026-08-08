using CardiTrack.Mobile.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CardiTrack.Mobile.Core.Auth;

/// <summary>
/// Owns token refresh for the whole app. Registered as a singleton so the SemaphoreSlim
/// gives single-flight refresh even though the HTTP handler chain is transient.
/// </summary>
public sealed class TokenRefresher : ITokenRefresher
{
    private static readonly TimeSpan ExpirySkew = TimeSpan.FromSeconds(30);

    private readonly ITokenStore _store;
    private readonly IAuth0AuthClient _auth0;
    private readonly Auth0Options _options;
    private readonly ILogger<TokenRefresher> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public event Action? SessionExpired;

    public TokenRefresher(
        ITokenStore store,
        IAuth0AuthClient auth0,
        Auth0Options options,
        ILogger<TokenRefresher>? logger = null)
    {
        _store = store;
        _auth0 = auth0;
        _options = options;
        _logger = logger ?? NullLogger<TokenRefresher>.Instance;
    }

    public async Task<string?> GetValidAccessTokenAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        var current = await _store.GetAsync();
        if (current is null)
            return null;
        if (!forceRefresh && IsStillValid(current))
            return current.AccessToken;

        await _gate.WaitAsync(ct);
        try
        {
            // Another caller may have refreshed while we waited on the gate — or ended the
            // session, which clears the store and is what stops a pile-up of concurrent 401s
            // from each raising SessionExpired.
            var latest = await _store.GetAsync();
            if (latest is null)
                return null;
            if (latest.AccessToken != current.AccessToken && IsStillValid(latest))
                return latest.AccessToken;

            if (string.IsNullOrEmpty(latest.RefreshToken))
            {
                await FailSessionAsync("no refresh token was stored at sign-in "
                    + "(Auth0 omits one unless the API allows offline access)");
                return null;
            }

            try
            {
                var fresh = await _auth0.RefreshAsync(latest.RefreshToken, ct);
                // Rotating refresh tokens: Auth0 may omit a new one — keep the old token then.
                fresh = fresh with { RefreshToken = fresh.RefreshToken ?? latest.RefreshToken };
                await _store.SaveAsync(fresh);
                _logger.LogInformation("Access token refreshed; valid for {ValidForSeconds}s",
                    (int)(fresh.ExpiresAt - DateTimeOffset.UtcNow).TotalSeconds);
                AccessTokenAudience.Warn(_logger, fresh.AccessToken, _options.Audience, "token refresh");
                return fresh.AccessToken;
            }
            catch (AuthException ex) when (ex.Code == AuthErrorCode.Network)
            {
                // Transient: keep the session, caller just fails this request.
                _logger.LogWarning("Token refresh failed with a network error; keeping the session");
                return null;
            }
            catch (AuthException ex)
            {
                // invalid_grant etc. — refresh token revoked/expired: session is over.
                await FailSessionAsync($"Auth0 rejected the refresh token ({ex.Auth0Error ?? ex.Code.ToString()})");
                return null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool IsStillValid(AuthTokens tokens) =>
        tokens.ExpiresAt > DateTimeOffset.UtcNow.Add(ExpirySkew);

    private async Task FailSessionAsync(string reason)
    {
        _logger.LogWarning("Session ended: {Reason}; clearing tokens and returning to sign-in", reason);
        await _store.ClearAsync();
        SessionExpired?.Invoke();
    }
}
