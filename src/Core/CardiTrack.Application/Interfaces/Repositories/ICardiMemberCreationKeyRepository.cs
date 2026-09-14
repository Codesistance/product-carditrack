using CardiTrack.Domain.Entities;

namespace CardiTrack.Application.Interfaces.Repositories;

public interface ICardiMemberCreationKeyRepository : IRepository<CardiMemberCreationKey>
{
    /// <summary>
    /// The member a previous attempt under this key produced, or null if this caregiver has not
    /// used the key. Scoped by caregiver in the query, so another user's key reads as unused
    /// rather than as theirs.
    /// </summary>
    /// <remarks>
    /// Untracked: the caller reads it to answer a retry, never to change it. A key row is written
    /// once and read afterwards.
    /// </remarks>
    Task<CardiMemberCreationKey?> FindAsync(Guid userId, string key, CancellationToken ct = default);

    /// <summary>
    /// Drops this caregiver's keys older than the cut-off. Their whole purpose is to answer a
    /// retry that follows within seconds, so a key from last month answers nothing and is only
    /// taking up room.
    /// </summary>
    /// <remarks>
    /// Scoped to one caregiver and run on the path that has just written a key, so the table
    /// trims itself as it is used rather than needing a job to visit it. A caregiver who never
    /// adds another member keeps a handful of rows, which is the cost of not adding a worker for
    /// three columns.
    /// </remarks>
    Task PurgeOlderThanAsync(Guid userId, DateTime cutoffUtc, CancellationToken ct = default);
}
