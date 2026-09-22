using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Interfaces.Repositories;

/// <summary>
/// Caregiver invitations (<see cref="CaregiverInvite"/>): an admin's app creates and watches them,
/// and the invitee's redemption resolves them.
/// </summary>
public interface ICaregiverInviteRepository : IRepository<CaregiverInvite>
{
    /// <summary>
    /// The invitation a token names, or null when none has that hash. Says nothing about whether it
    /// is live — an expired or already-spent invitation comes back here and the caller decides,
    /// because the landing page has to tell those cases apart to write the right page.
    /// </summary>
    Task<CaregiverInvite?> GetByTokenHashAsync(string tokenHash, CancellationToken ct = default);

    /// <summary>Every invitation issued for this member, newest first.</summary>
    Task<IReadOnlyList<CaregiverInvite>> GetForMemberAsync(
        Guid cardiMemberId, CancellationToken ct = default);

    /// <summary>
    /// Moves an invitation to a terminal state, in the database, only if it is still in one of
    /// <paramref name="from"/>. Returns whether this call was the one that moved it.
    /// </summary>
    /// <remarks>
    /// A read-then-write would let two racing requests both decide an invitation was still open —
    /// the invitee double-tapping Accept, or accepting while the admin revokes. The precondition
    /// travels in the UPDATE's WHERE clause so exactly one can win, and the loser is told the
    /// invitation is no longer live rather than quietly overwriting the winner's outcome.
    /// </remarks>
    Task<bool> TryResolveAsync(
        Guid inviteId,
        IReadOnlyCollection<CaregiverInviteStatus> from,
        CaregiverInviteStatus to,
        DateTime resolvedAt,
        Guid? acceptedByUserId,
        CancellationToken ct = default);

    /// <summary>
    /// Marks an invitation opened, if it is still Pending. Somebody reloading the landing page is
    /// not an error, so a false return means nothing more than "it was already opened".
    /// </summary>
    Task<bool> TryMarkOpenedAsync(Guid inviteId, DateTime openedAt, CancellationToken ct = default);
}
