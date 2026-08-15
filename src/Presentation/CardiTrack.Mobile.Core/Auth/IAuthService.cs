namespace CardiTrack.Mobile.Core.Auth;

public interface IAuthService
{
    Task SignInAsync(string email, string password, CancellationToken ct = default);

    /// <summary>Signs in via Auth0 Universal Login in the system browser (Authorization Code
    /// + PKCE) for a social connection — Auth0Options.GoogleConnection / AppleConnection.
    /// Covers both sign-in and sign-up: the provider flow is the same operation.</summary>
    Task SignInWithProviderAsync(string connection, CancellationToken ct = default);

    /// <summary>Creates the Auth0 account only — sign-in is gated on email verification.</summary>
    Task SignUpAsync(string name, string email, string password, CancellationToken ct = default);

    Task RequestPasswordResetAsync(string email, CancellationToken ct = default);

    /// <summary>
    /// Restores the session from stored tokens. True when a session still exists locally —
    /// including when a token refresh could not reach the network, so the app can open onto
    /// cached data. False means show login.
    /// </summary>
    Task<bool> TrySilentSignInAsync(CancellationToken ct = default);

    Task SignOutAsync(CancellationToken ct = default);

    /// <summary>Display name from the id token, when a session is active.</summary>
    string? CurrentUserName { get; }

    string? CurrentUserEmail { get; }

    /// <summary>Auth0's email_verified from the id token; null when no session/claim.</summary>
    bool? IsEmailVerified { get; }
}
