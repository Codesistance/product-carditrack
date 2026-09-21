using CardiTrack.Domain.Entities;

namespace CardiTrack.Application.Interfaces.Repositories;

public interface IMemberAdviseObservationRepository : IRepository<MemberAdviseObservation>
{
    /// <summary>
    /// This member's observations inside the window, newest first, capped at
    /// <paramref name="limit"/>. Untracked: every caller is a read path, and the one writer
    /// appends rather than editing what is already there.
    /// </summary>
    /// <param name="fromUtc">Inclusive lower bound.</param>
    /// <param name="toUtc">Exclusive upper bound, so adjacent windows neither overlap nor gap.</param>
    Task<IReadOnlyList<MemberAdviseObservation>> GetByCardiMemberAsync(
        Guid cardiMemberId, DateTime fromUtc, DateTime toUtc, int limit, CancellationToken ct = default);

    /// <summary>
    /// The newest entry this member has for each topic, or an empty list when they have none —
    /// what the generator compares a fresh observation against to decide whether it is saying
    /// anything new.
    /// </summary>
    /// <remarks>
    /// The log's own last word, deliberately, rather than the current <c>MemberAdvise</c> row.
    /// The two disagree in two cases that matter: a topic whose advise row was withdrawn and
    /// later came back would look new against the (absent) row while the log plainly holds the
    /// same sentence, and two passes racing each other both read the same pre-update row while
    /// only one of them can have written the newest log entry.
    /// </remarks>
    Task<IReadOnlyList<MemberAdviseObservation>> GetLatestPerTopicAsync(
        Guid cardiMemberId, CancellationToken ct = default);

    /// <summary>
    /// Entries observed before <paramref name="cutoffUtc"/>, oldest first, up to
    /// <paramref name="take"/> — the retention sweep's selection pass.
    /// </summary>
    Task<IReadOnlyList<MemberAdviseObservation>> GetObservedBeforeAsync(DateTime cutoffUtc, int take);

    /// <summary>
    /// Deletes the named entries, restating the cutoff server-side. Returns how many went.
    /// </summary>
    Task<int> DeleteObservedBeforeAsync(IReadOnlyCollection<Guid> ids, DateTime cutoffUtc);
}
