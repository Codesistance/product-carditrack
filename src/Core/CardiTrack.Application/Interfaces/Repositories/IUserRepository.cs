using CardiTrack.Domain.Entities;

namespace CardiTrack.Application.Interfaces.Repositories;

public interface IUserRepository : IRepository<User>
{
    Task<User?> GetByAuth0UserIdAsync(string auth0UserId);
    Task<User?> GetByEmailAsync(string email);
    Task UpdateLastLoginAsync(Guid userId);

    /// <summary>
    /// Stamps the health-data disclosure as dismissed at <paramref name="dismissedAtUtc"/> — but
    /// only if it has not been stamped already, and in one statement, so two devices
    /// acknowledging at once cannot overwrite each other. The first acknowledgement is the
    /// compliance record. True when this call was the one that recorded it; false when it was
    /// already recorded or there is no such user.
    /// </summary>
    Task<bool> TryRecordHealthDataDisclosureDismissalAsync(string auth0UserId, DateTime dismissedAtUtc);
}
