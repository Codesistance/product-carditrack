namespace CardiTrack.Mobile.Core.Auth;

public sealed class AuthService : IAuthService
{
    private readonly IAuth0AuthClient _auth0;
    private readonly ITokenStore _store;
    private readonly ITokenRefresher _refresher;

    private IReadOnlyDictionary<string, string> _claims =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public AuthService(IAuth0AuthClient auth0, ITokenStore store, ITokenRefresher refresher)
    {
        _auth0 = auth0;
        _store = store;
        _refresher = refresher;
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
    }

    public async Task SignUpAsync(string name, string email, string password, CancellationToken ct = default)
    {
        await _auth0.SignUpAsync(name, email, password, ct);
        // The Auth0 account now exists even if this login fails — the user can sign in manually.
        await SignInAsync(email, password, ct);
    }

    public Task RequestPasswordResetAsync(string email, CancellationToken ct = default) =>
        _auth0.RequestPasswordResetAsync(email, ct);

    public async Task<bool> TrySilentSignInAsync(CancellationToken ct = default)
    {
        var accessToken = await _refresher.GetValidAccessTokenAsync(forceRefresh: false, ct);
        if (accessToken is null)
            return false;

        var tokens = await _store.GetAsync();
        _claims = JwtPayloadReader.ReadClaims(tokens?.IdToken);
        return true;
    }

    public async Task SignOutAsync(CancellationToken ct = default)
    {
        var tokens = await _store.GetAsync();
        if (!string.IsNullOrEmpty(tokens?.RefreshToken))
            await _auth0.RevokeAsync(tokens.RefreshToken, ct);
        await _store.ClearAsync();
        _claims = new Dictionary<string, string>(StringComparer.Ordinal);
    }
}
