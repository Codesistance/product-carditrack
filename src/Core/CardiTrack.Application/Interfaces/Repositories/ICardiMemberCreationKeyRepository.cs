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
    /// Drops this caregiver's keys older than the cut-off.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Trimming, not expiry.</strong> <see cref="FindAsync"/> does not filter on age, and
    /// this runs only when the same caregiver creates another member — so a key that nothing has
    /// swept stays answerable, and one swept an hour ago is gone. Saying it any other way would
    /// describe a guarantee the code does not make.
    /// </para>
    /// <para>
    /// Enforcing the cut-off in <see cref="FindAsync"/> instead would be worse, not better: the
    /// mobile form persists its key with the draft, so a caregiver reopening a days-old draft is
    /// making exactly the retry this exists for. An expiring lookup would ignore their key and
    /// create a second member — while the surviving row made the insert collide on the unique
    /// index. The cut-off is set to outlive the draft that carries the key.
    /// </para>
    /// <para>
    /// Scoped to one caregiver and run on the path that has just written a key, so the table trims
    /// itself as it is used rather than needing a job to visit it. A caregiver who never adds
    /// another member keeps a handful of rows, which is the cost of not adding a worker for three
    /// columns.
    /// </para>
    /// </remarks>
    Task PurgeOlderThanAsync(Guid userId, DateTime cutoffUtc, CancellationToken ct = default);
}
