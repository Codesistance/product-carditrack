using CardiTrack.Domain.Entities;

namespace CardiTrack.Application.Interfaces.Repositories;

public interface IOrganizationRepository : IRepository<Organization>
{
    Task<Organization?> GetWithSubscriptionAsync(Guid id);

    /// <summary>
    /// Hard-deletes organizations older than <paramref name="minAge"/> that no user
    /// or CardiMember references (abandoned onboardings). Their trial subscriptions
    /// go with them via the FK cascade. Returns the number of organizations removed.
    /// </summary>
    Task<int> DeleteOrphanedAsync(TimeSpan minAge);
}
