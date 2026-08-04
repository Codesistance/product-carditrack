namespace CardiTrack.Mobile.Core.Auth;

public interface IAuthService
{
    Task SignInAsync(string email, string password, CancellationToken ct = default);

    /// <summary>Creates the Auth0 account, then signs in with the same credentials.</summary>
    Task SignUpAsync(string name, string email, string password, CancellationToken ct = default);

    Task RequestPasswordResetAsync(string email, CancellationToken ct = default);

    /// <summary>Restores the session from the stored refresh token. False = show login.</summary>
    Task<bool> TrySilentSignInAsync(CancellationToken ct = default);

    Task SignOutAsync(CancellationToken ct = default);

    /// <summary>Display name from the id token, when a session is active.</summary>
    string? CurrentUserName { get; }

    string? CurrentUserEmail { get; }

    /// <summary>Auth0's email_verified from the id token; null when no session/claim.</summary>
    bool? IsEmailVerified { get; }
}
