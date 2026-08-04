namespace CardiTrack.Application.Interfaces.Services;

public interface IAuth0ManagementService
{
    /// <summary>
    /// Best-effort resend of Auth0's verification email for the account with this email.
    /// Never throws and never reveals whether the account exists — failures are logged
    /// and swallowed so the anonymous endpoint can always answer success.
    /// </summary>
    Task TrySendVerificationEmailAsync(string email, CancellationToken ct = default);
}
