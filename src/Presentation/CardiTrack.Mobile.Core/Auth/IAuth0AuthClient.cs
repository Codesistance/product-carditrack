namespace CardiTrack.Mobile.Core.Auth;

public interface IAuth0AuthClient
{
    Task<AuthTokens> LoginAsync(string email, string password, CancellationToken ct = default);

    Task SignUpAsync(string name, string email, string password, CancellationToken ct = default);

    Task RequestPasswordResetAsync(string email, CancellationToken ct = default);

    Task<AuthTokens> RefreshAsync(string refreshToken, CancellationToken ct = default);

    Task RevokeAsync(string refreshToken, CancellationToken ct = default);

    /// <summary>Universal Login /authorize URL for a social connection (Authorization Code + PKCE).</summary>
    Uri BuildAuthorizeUri(string connection, string codeChallenge, string state);

    /// <summary>Redeems the /authorize code at /oauth/token with the PKCE verifier.</summary>
    Task<AuthTokens> ExchangeAuthorizationCodeAsync(string code, string codeVerifier, CancellationToken ct = default);
}
