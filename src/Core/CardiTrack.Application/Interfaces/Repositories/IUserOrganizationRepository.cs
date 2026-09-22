using CardiTrack.Domain.Entities;

namespace CardiTrack.Application.Interfaces.Repositories;

public interface IUserOrganizationRepository : IRepository<UserOrganization>
{
    /// <summary>Every family this person is in, active or not; callers filter on <c>IsActive</c>.</summary>
    Task<IEnumerable<UserOrganization>> GetByUserIdAsync(Guid userId);

    /// <summary>Everyone in one family, active or not.</summary>
    Task<IEnumerable<UserOrganization>> GetByOrganizationIdAsync(Guid organizationId);

    /// <summary>The one row for this person in this family, or null if they were never in it.</summary>
    Task<UserOrganization?> GetAsync(Guid userId, Guid organizationId);
}
