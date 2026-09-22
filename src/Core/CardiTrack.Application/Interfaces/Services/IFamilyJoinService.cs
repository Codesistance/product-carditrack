using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;

namespace CardiTrack.Application.Interfaces.Services;

/// <summary>
/// The Family ID route into a family: somebody asks, an admin decides what they get.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Approval is the access control, not the code.</strong> A Family ID is short enough to
/// read down the phone and therefore short enough to guess, so nothing here treats knowing one as
/// evidence of anything. <see cref="RequestAsync"/> tells the asker only that their request was
/// recorded — never whether the family exists, never its name, never who is in it — so a search
/// through the code space returns the same answer whatever it hits.
/// </para>
/// <para>
/// That is also why the request carries no grant. What an approved person can see is chosen by the
/// admin at <see cref="ApproveAsync"/>, per member, in the same act that lets them in.
/// </para>
/// </remarks>
public interface IFamilyJoinService
{
    /// <summary>
    /// Records a request to join the family with this Family ID.
    /// </summary>
    /// <returns>
    /// Always a receipt, never a verdict on whether the family exists. Asking again while a
    /// request is live returns the one already outstanding rather than queueing a second.
    /// </returns>
    Task<FamilyJoinRequestReceipt> RequestAsync(
        Guid requestingUserId, JoinFamilyRequest request, CancellationToken ct = default);

    /// <summary>Requests this person has outstanding, for their own screen.</summary>
    Task<IReadOnlyList<FamilyJoinRequestSummary>> GetMineAsync(
        Guid requestingUserId, CancellationToken ct = default);

    /// <summary>The asker changes their mind. Terminal, and safe to call twice.</summary>
    Task WithdrawAsync(Guid requestingUserId, Guid requestId, CancellationToken ct = default);

    /// <summary>Requests waiting on this family's admin. Admin-only.</summary>
    Task<IReadOnlyList<PendingJoinRequest>> GetPendingAsync(
        Guid requestingUserId, Guid organizationId, CancellationToken ct = default);

    /// <summary>
    /// Lets somebody in, with the members and role the admin chose. Admin-only, and one act: the
    /// membership and every grant are written together, so nobody is ever in a family with an
    /// access decision still pending.
    /// </summary>
    Task ApproveAsync(
        Guid requestingUserId,
        Guid organizationId,
        Guid requestId,
        ApproveJoinRequest decision,
        CancellationToken ct = default);

    /// <summary>Says no. Admin-only, and tells the asker nothing beyond the outcome.</summary>
    Task DeclineAsync(
        Guid requestingUserId, Guid organizationId, Guid requestId, CancellationToken ct = default);
}
