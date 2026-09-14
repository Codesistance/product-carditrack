using CardiTrack.Domain.Enums;

namespace CardiTrack.API.Infrastructure.UserContext;

public interface IUserContext
{
    Guid UserId { get; }
    string Auth0UserId { get; }
    Guid OrganizationId { get; }
    string Email { get; }
    UserRole Role { get; }
    bool IsAuthenticated { get; }
    string Locale { get; }

    /// <summary>
    /// Auth0's email_verified from the access token (needs the post-login Action per
    /// the Auth0 runbook). Null when the claim is absent — treat as "unknown", not false.
    /// </summary>
    bool? EmailVerified { get; }

    /// <summary>
    /// When this caregiver asked for their account to be deleted, or null if they have not.
    /// </summary>
    /// <remarks>
    /// Read from the user row the context middleware already loads, so the gate that uses it
    /// costs no extra query. An account with this set is refused everything but reading and
    /// cancelling its own deletion — see <c>PendingDeletionGateMiddleware</c>.
    /// </remarks>
    DateTime? DeletionRequestedAtUtc { get; }
}
