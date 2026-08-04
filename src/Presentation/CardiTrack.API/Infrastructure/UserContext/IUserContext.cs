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
}
