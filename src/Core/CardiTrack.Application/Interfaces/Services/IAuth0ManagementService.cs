namespace CardiTrack.Application.Interfaces.Services;

public interface IAuth0ManagementService
{
    /// <summary>
    /// Best-effort resend of Auth0's verification email for the account with this email.
    /// Never throws and never reveals whether the account exists — failures are logged
    /// and swallowed so the anonymous endpoint can always answer success.
    /// </summary>
    Task TrySendVerificationEmailAsync(string email, CancellationToken ct = default);

    /// <summary>
    /// Best-effort delete (or, if delete is refused, block) of the Auth0 user that this
    /// account was. Never throws — a provider outage must not roll back a Postgres erasure
    /// that has already committed. Failures are logged so an operator can finish the job.
    /// </summary>
    Task TryDeleteUserAsync(string auth0UserId, CancellationToken ct = default);
}
