using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;

namespace CardiTrack.Application.Interfaces.Services;

/// <summary>
/// Caregiver invitations: an admin offers another adult a share of the watching, and the invitee
/// redeems the offer once they have an account.
/// </summary>
/// <remarks>
/// The counterpart of <see cref="IDeviceConnectionInviteService"/>, and deliberately shaped like
/// it — with one difference that runs through every method here. A wearer's invitation ends
/// anonymously; this one cannot. <see cref="RedeemAsync"/> takes an authenticated user id because
/// the token's whole authority is to create a membership and a grant <em>for somebody the system
/// already knows</em>. Nothing on this interface will return health data for a token alone.
/// </remarks>
public interface ICaregiverInviteService
{
    /// <summary>
    /// Mints an invitation for one member and returns it with the one-time URL to hand over.
    /// Admin-only: the caller must hold an active Admin membership of the member's family.
    /// </summary>
    Task<CaregiverInviteResponse> CreateAsync(
        Guid requestingUserId,
        Guid cardiMemberId,
        CreateCaregiverInviteRequest request,
        string requestBaseUrl,
        CancellationToken ct = default);

    /// <summary>Every invitation issued for this member, newest first. Admin-only.</summary>
    Task<IReadOnlyList<CaregiverInviteResponse>> ListAsync(
        Guid requestingUserId, Guid cardiMemberId, CancellationToken ct = default);

    /// <summary>Withdraws a live invitation. Admin-only, and idempotent on an already-terminal one.</summary>
    Task<CaregiverInviteResponse> RevokeAsync(
        Guid requestingUserId, Guid cardiMemberId, Guid inviteId, CancellationToken ct = default);

    /// <summary>
    /// What the landing page may show for a token, or null if it names nothing live. Marks the
    /// invitation opened as a side effect, so the admin's list can show it was seen.
    /// </summary>
    Task<CaregiverInviteView?> ViewAsync(string token, CancellationToken ct = default);

    /// <summary>
    /// Redeems an invitation for an authenticated user: creates or reactivates their membership of
    /// the family and their grant on the member, then resolves the invitation.
    /// </summary>
    /// <remarks>
    /// Re-checks that the issuer still holds the authority they had when they issued it — an
    /// invitation must not outlive it — and refuses a token whose invitation is expired, spent or
    /// withdrawn. Redeeming twice is not an error for the person who already holds the grant; it
    /// simply returns what they have, without widening it.
    /// </remarks>
    Task<CaregiverInviteRedemption> RedeemAsync(
        string token, Guid redeemingUserId, CancellationToken ct = default);

    /// <summary>Records that the invitee said no. Terminal, and safe to call twice.</summary>
    Task<bool> DeclineAsync(string token, CancellationToken ct = default);
}
