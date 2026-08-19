using CardiTrack.Domain.Entities;

namespace CardiTrack.Application.Interfaces.Repositories;

public interface ICardiMemberRepository : IRepository<CardiMember>
{
    Task<IEnumerable<CardiMember>> GetByOrganizationIdAsync(Guid organizationId);
    Task<CardiMember?> GetWithRelationshipsAsync(Guid id);

    /// <summary>
    /// Ids of active members with at least one activity log on or after <paramref name="since"/>.
    /// Ids rather than entities because the caller processes members one scope at a time, and
    /// filtered rather than "all active" so dormant records are not rescanned on every run.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetActiveIdsWithActivitySinceAsync(DateOnly since);

    /// <summary>
    /// Ids of active members who have explicitly granted
    /// <see cref="Domain.Entities.CardiMember.EnvironmentalContextConsentGranted"/>. The sole
    /// candidate filter for the environmental-enrichment pass — a member absent from this list
    /// is never looked at by that pass, full stop.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetActiveIdsWithEnvironmentalConsentAsync();

    /// <summary>
    /// Every <see cref="Domain.Entities.CardiMember.PhotoObjectName"/> carried by an active
    /// member — the reference set <c>OrphanedPhotoCleanupWorker</c> diffs the photo bucket
    /// against. Names rather than entities: the sweep needs membership tests, nothing else.
    /// </summary>
    Task<IReadOnlyList<string>> GetActivePhotoObjectNamesAsync();

    /// <summary>
    /// Soft-deleted members that still carry a <c>PhotoObjectName</c>. Normally empty — the
    /// removal path clears the column before it saves — so a hit is a crashed removal whose
    /// photo outlived the membership; the cleanup worker deletes the blob and nulls the column.
    /// Entities rather than ids because the caller updates the row it was handed.
    /// </summary>
    Task<IReadOnlyList<CardiMember>> GetInactiveWithPhotoAsync();
}
