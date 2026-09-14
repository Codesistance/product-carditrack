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

    /// <summary>
    /// Stamps the account as awaiting deletion, if it is not already. Returns false when a request
    /// is already outstanding, so a second tap does not restart the 30 days.
    /// </summary>
    /// <remarks>
    /// A restarted clock would be the wrong behaviour in the one direction that matters: it would
    /// keep the data longer than the first request promised.
    /// </remarks>
    Task<bool> TryRequestDeletionAsync(string auth0UserId, DateTime requestedAtUtc);

    /// <summary>
    /// Clears an outstanding deletion request. Returns false when there was nothing to cancel.
    /// </summary>
    Task<bool> TryCancelDeletionAsync(string auth0UserId);

    /// <summary>
    /// The accounts whose cancellation window has closed — a deletion was requested at or before
    /// <paramref name="cutoffUtc"/> and has not been cancelled since. Oldest request first, so a
    /// bounded run always takes the account that has been waiting longest.
    /// </summary>
    /// <remarks>
    /// The cutoff is passed in rather than computed here because the grace period belongs to
    /// <c>UserService.DeletionGracePeriod</c> — the same constant the app quotes to a caregiver
    /// as the date they can cancel until. A second copy of it inside a query is the shape that
    /// erases an account somebody still had the right to keep.
    /// </remarks>
    Task<IReadOnlyList<Guid>> GetAccountsDueForErasureAsync(DateTime cutoffUtc, int limit);
}
