using CardiTrack.Mobile.Core.Configuration;
using CardiTrack.Mobile.Core.Offline;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CardiTrack.Mobile.Core.Auth;

public sealed class AuthService : IAuthService
{
    private readonly IAuth0AuthClient _auth0;
    private readonly ITokenStore _store;
    private readonly ITokenRefresher _refresher;
    private readonly IBrowserAuthenticator _browser;
    private readonly Auth0Options _options;
    private readonly IOfflineReadCache? _cache;
    private readonly ILogger<AuthService> _logger;

    private IReadOnlyDictionary<string, string> _claims =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public AuthService(
        IAuth0AuthClient auth0,
        ITokenStore store,
        ITokenRefresher refresher,
        IBrowserAuthenticator browser,
        Auth0Options options,
        IOfflineReadCache? cache = null,
        ILogger<AuthService>? logger = null)
    {
        _auth0 = auth0;
        _store = store;
        _refresher = refresher;
        _browser = browser;
        _options = options;
        _cache = cache;
        _logger = logger ?? NullLogger<AuthService>.Instance;
    }

    public string? CurrentUserName =>
        _claims.TryGetValue("name", out var name) && !string.IsNullOrWhiteSpace(name) ? name : null;

    public string? CurrentUserEmail =>
        _claims.TryGetValue("email", out var email) && !string.IsNullOrWhiteSpace(email) ? email : null;

    /// <summary>From the ID token; null before sign-in. Refreshes with the session, so it can
    /// lag until the next launch after the user clicks Auth0's verification link.</summary>
    public bool? IsEmailVerified =>
        _claims.TryGetValue("email_verified", out var verified) && bool.TryParse(verified, out var value)
            ? value
            : null;

    public async Task SignInAsync(string email, string password, CancellationToken ct = default)
    {
        var tokens = await _auth0.LoginAsync(email, password, ct);
        await _store.SaveAsync(tokens);
        _claims = JwtPayloadReader.ReadClaims(tokens.IdToken);
        // Checked here as well as on refresh: a build stamped with the wrong audience is
        // wrong from the very first token, and this is the only place that sees one issued.
        AccessTokenAudience.Warn(_logger, tokens.AccessToken, _options.Audience, "sign-in");
    }

    public async Task SignInWithProviderAsync(string connection, CancellationToken ct = default)
    {
        var verifier = Pkce.CreateVerifier();
        var state = Pkce.CreateState();
        var authorizeUri = _auth0.BuildAuthorizeUri(connection, Pkce.CreateChallenge(verifier), state);

        // TaskCanceledException (sheet dismissed) and PlatformNotSupportedException
        // propagate to the caller untouched — the pages handle both (Fitbit precedent).
        var callback = await _browser.AuthenticateAsync(authorizeUri, new Uri(Auth0Options.CallbackUri), ct);

        // State first — nothing else in the callback is trusted before it.
        callback.TryGetValue("state", out var returnedState);
        if (!string.Equals(returnedState, state, StringComparison.Ordinal))
        {
            _logger.LogWarning("Social sign-in callback rejected: state mismatch");
            throw new AuthException(AuthErrorCode.Unknown, "Sign-in failed. Please try again.");
        }

        if (callback.TryGetValue("error", out var error) && !string.IsNullOrEmpty(error))
        {
            callback.TryGetValue("error_description", out var description);
            var mapped = MapAuthorizeError(error, description);
            _logger.LogWarning("Social sign-in denied at /authorize: {AuthErrorCode} ({Auth0Error})",
                mapped.Code, error);
            throw mapped;
        }

        if (!callback.TryGetValue("code", out var code) || string.IsNullOrEmpty(code))
            throw new AuthException(AuthErrorCode.Unknown, "Sign-in failed. Please try again.");

        var tokens = await _auth0.ExchangeAuthorizationCodeAsync(code, verifier, ct);
        await _store.SaveAsync(tokens);
        _claims = JwtPayloadReader.ReadClaims(tokens.IdToken);
        AccessTokenAudience.Warn(_logger, tokens.AccessToken, _options.Audience, "social-sign-in");
    }

    private static AuthException MapAuthorizeError(string error, string? description) => error switch
    {
        // Defensive only: the tenant Action short-circuits the verification deny for
        // social connections, so this fires only if that Action change isn't deployed.
        "access_denied" when description is not null
            && (description.Equals("email_not_verified", StringComparison.OrdinalIgnoreCase)
                || description.Contains("verify", StringComparison.OrdinalIgnoreCase))
            => new AuthException(AuthErrorCode.EmailNotVerified,
                "Verify your email to continue — check your inbox for the link.", error, description),
        "access_denied"
            => new AuthException(AuthErrorCode.Unknown, "Sign-in was not completed.", error, description),
        // The tenant has no such connection, or it isn't enabled for this application
        // (Apple before its credentials are provisioned). Note: a disabled connection can
        // also render an Auth0 error page instead of redirecting — the user closing that
        // page surfaces as TaskCanceledException and is silently absorbed by the caller.
        "invalid_request" when description?.Contains("connection", StringComparison.OrdinalIgnoreCase) == true
            => new AuthException(AuthErrorCode.ProviderUnavailable,
                "This sign-in method isn't available yet.", error, description),
        _ => new AuthException(AuthErrorCode.Unknown, "Sign-in failed. Please try again.", error, description),
    };

    public Task SignUpAsync(string name, string email, string password, CancellationToken ct = default) =>
        // No auto-login: the tenant denies unverified logins (hard gate), and a
        // seconds-old account is never verified. The app routes to VerifyEmailPage,
        // which signs in once the user has clicked the link.
        _auth0.SignUpAsync(name, email, password, ct);

    public Task RequestPasswordResetAsync(string email, CancellationToken ct = default) =>
        _auth0.RequestPasswordResetAsync(email, ct);

    public async Task<bool> TrySilentSignInAsync(CancellationToken ct = default)
    {
        // Refresh if the access token is stale; a network miss leaves the store alone, while
        // a rejected refresh token clears it. The store — not the access token — is the
        // session: an expired bearer is still a signed-in caregiver who can read cached data.
        _ = await _refresher.GetValidAccessTokenAsync(forceRefresh: false, ct);
        var tokens = await _store.GetAsync();
        if (tokens is null)
            return false;

        _claims = JwtPayloadReader.ReadClaims(tokens.IdToken);
        return true;
    }

    public async Task SignOutAsync(CancellationToken ct = default)
    {
        var tokens = await _store.GetAsync();
        if (!string.IsNullOrEmpty(tokens?.RefreshToken))
        {
            try
            {
                await _auth0.RevokeAsync(tokens.RefreshToken, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Local sign-out must still happen offline. Revoke is best-effort — the
                // live Auth0 client already swallows transport errors, but the interface
                // does not promise AuthException-only, and a misconfigured build must not
                // leave tokens and cached health data on the device.
                _logger.LogWarning(ex, "Token revoke failed; clearing the local session anyway");
            }
        }

        await _store.ClearAsync();
        if (_cache is not null)
            await _cache.ClearAsync(ct);
        _claims = new Dictionary<string, string>(StringComparer.Ordinal);
    }
}
