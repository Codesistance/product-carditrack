using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Interfaces.Repositories;

/// <summary>Join requests: somebody asking to be let into a family, and an admin answering.</summary>
public interface IFamilyJoinRequestRepository : IRepository<FamilyJoinRequest>
{
    /// <summary>Requests waiting on this family's admin, oldest first.</summary>
    Task<IReadOnlyList<FamilyJoinRequest>> GetPendingForOrganizationAsync(
        Guid organizationId, DateTime utcNow, CancellationToken ct = default);

    /// <summary>Everything this person has asked for and not yet finished with, newest first.</summary>
    Task<IReadOnlyList<FamilyJoinRequest>> GetForUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>This person's live request to this family, if they have one.</summary>
    Task<FamilyJoinRequest?> GetLiveAsync(
        Guid userId, Guid organizationId, DateTime utcNow, CancellationToken ct = default);

    /// <summary>
    /// Moves a request to a terminal state, in the database, only if it is still Pending. Returns
    /// whether this call was the one that moved it — two admins answering at once is a race the
    /// database settles, not one a read-then-write could.
    /// </summary>
    Task<bool> TryResolveAsync(
        Guid requestId,
        FamilyJoinRequestStatus to,
        Guid? resolvedByUserId,
        DateTime resolvedAt,
        CancellationToken ct = default);
}
