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
}
