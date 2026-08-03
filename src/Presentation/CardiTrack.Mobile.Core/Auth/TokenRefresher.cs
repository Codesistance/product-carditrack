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
    private readonly SemaphoreSlim _gate = new(1, 1);

    public event Action? SessionExpired;

    public TokenRefresher(ITokenStore store, IAuth0AuthClient auth0)
    {
        _store = store;
        _auth0 = auth0;
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
            // Another caller may have refreshed while we waited on the gate.
            var latest = await _store.GetAsync();
            if (latest is null)
                return null;
            if (latest.AccessToken != current.AccessToken && IsStillValid(latest))
                return latest.AccessToken;

            if (string.IsNullOrEmpty(latest.RefreshToken))
            {
                await FailSessionAsync();
                return null;
            }

            try
            {
                var fresh = await _auth0.RefreshAsync(latest.RefreshToken, ct);
                // Rotating refresh tokens: Auth0 may omit a new one — keep the old token then.
                fresh = fresh with { RefreshToken = fresh.RefreshToken ?? latest.RefreshToken };
                await _store.SaveAsync(fresh);
                return fresh.AccessToken;
            }
            catch (AuthException ex) when (ex.Code == AuthErrorCode.Network)
            {
                // Transient: keep the session, caller just fails this request.
                return null;
            }
            catch (AuthException)
            {
                // invalid_grant etc. — refresh token revoked/expired: session is over.
                await FailSessionAsync();
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

    private async Task FailSessionAsync()
    {
        await _store.ClearAsync();
        SessionExpired?.Invoke();
    }
}
