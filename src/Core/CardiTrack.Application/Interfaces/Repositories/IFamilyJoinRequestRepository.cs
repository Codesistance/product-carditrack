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
    /// Adds a pending request, or returns the one already outstanding when this call loses the
    /// race to create it. Either way the caller gets the request that is now live.
    /// </summary>
    /// <remarks>
    /// Asking twice is a double tap rather than a second request, and the service reads for a live
    /// one before inserting — but that read and the insert are two statements, so two taps a
    /// moment apart can both find nothing. The filtered unique index settles it and hands the
    /// loser a conflict, which without this would surface as a 500 for what the person holding
    /// the phone experienced as one tap. Lives here rather than in the service because catching
    /// the conflict means naming a database exception type, and Application does not reference
    /// EF Core — the same reason <see cref="TryResolveAsync"/> is a repository concern.
    /// </remarks>
    Task<FamilyJoinRequest> AddOrGetLiveAsync(
        FamilyJoinRequest request, DateTime utcNow, CancellationToken ct = default);

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
