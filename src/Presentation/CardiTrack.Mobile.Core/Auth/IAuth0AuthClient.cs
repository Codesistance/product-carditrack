namespace CardiTrack.Mobile.Core.Auth;

public interface IAuth0AuthClient
{
    Task<AuthTokens> LoginAsync(string email, string password, CancellationToken ct = default);

    Task SignUpAsync(string name, string email, string password, CancellationToken ct = default);

    Task RequestPasswordResetAsync(string email, CancellationToken ct = default);

    Task<AuthTokens> RefreshAsync(string refreshToken, CancellationToken ct = default);

    Task RevokeAsync(string refreshToken, CancellationToken ct = default);
}
