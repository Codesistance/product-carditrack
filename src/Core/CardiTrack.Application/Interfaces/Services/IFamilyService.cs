using CardiTrack.Application.DTOs.Responses;

namespace CardiTrack.Application.Interfaces.Services;

/// <summary>
/// A family's people: who is in it, what they may do, and how somebody leaves.
/// </summary>
/// <remarks>
/// <para>
/// The invariant every method here defends is that a family has <strong>exactly one Admin</strong>,
/// and that Admin is its payer. It is why promotion and demotion are not separate operations:
/// making somebody else the Admin necessarily stops you being one, so
/// <see cref="TransferAdminAsync"/> does both in a single save rather than offering two calls a
/// caller could interleave and leave the family with two admins or none.
/// </para>
/// <para>
/// The same invariant is what makes leaving conditional. An Admin cannot simply go — they hand the
/// family on first (<see cref="LeaveAsync"/> refuses them), because with no Admin a family has
/// nobody to invite, approve, or pay for it.
/// </para>
/// </remarks>
public interface IFamilyService
{
    /// <summary>Every family the caller belongs to, with their role in each.</summary>
    Task<IReadOnlyList<FamilySummary>> GetMineAsync(Guid requestingUserId, CancellationToken ct = default);

    /// <summary>
    /// Everyone in one family. Any active member may read it — knowing who else is watching with
    /// you is not an administrative privilege.
    /// </summary>
    Task<IReadOnlyList<FamilyMemberSummary>> GetMembersAsync(
        Guid requestingUserId, Guid organizationId, CancellationToken ct = default);

    /// <summary>
    /// Hands the family, and its plan, to another active member. The caller becomes a Member in the
    /// same save. Admin-only.
    /// </summary>
    Task<IReadOnlyList<FamilyMemberSummary>> TransferAdminAsync(
        Guid requestingUserId, Guid organizationId, Guid newAdminUserId, CancellationToken ct = default);

    /// <summary>
    /// Removes another person from the family: their membership is deactivated and every grant they
    /// held on that family's members goes with it. Admin-only, and an Admin cannot remove
    /// themselves — that is <see cref="LeaveAsync"/>, which has its own rule.
    /// </summary>
    Task RemoveMemberAsync(
        Guid requestingUserId, Guid organizationId, Guid userId, CancellationToken ct = default);

    /// <summary>
    /// The caller leaves a family they are in, giving up their grants on its members.
    /// </summary>
    /// <remarks>
    /// Refused for the Admin: they hand the family on first. The one exception is an Admin who is
    /// the only person in it, where leaving is not a family matter at all but closing an account,
    /// and the caller is told to use that flow rather than being quietly left in place.
    /// </remarks>
    Task LeaveAsync(Guid requestingUserId, Guid organizationId, CancellationToken ct = default);
}
