using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Interfaces.Repositories;

/// <summary>
/// Read/write surface over the partitioned summary table. Not an <see cref="IRepository{T}"/> —
/// composite-keyed and partitioned, like the granular tables.
/// </summary>
public interface IDigestRepository
{
    /// <summary>
    /// Appends a generation. Summaries are history now, so this never overwrites an earlier one:
    /// only a re-run that collides on the whole natural key — the same member, day, audience and
    /// generation instant — is absorbed, which is the concurrent-execution case, not a rewrite.
    /// </summary>
    /// <returns>
    /// True when a row was inserted; false when a concurrent run already held the key and the
    /// insert was absorbed.
    /// </returns>
    Task<bool> AddAsync(DigestEntry entry, CancellationToken ct = default);

    /// <summary>
    /// The most recent summary for one member's local day, or null when none was generated.
    /// </summary>
    Task<DigestEntry?> GetLatestByDateAsync(
        Guid cardiMemberId, DateOnly localDate, DigestAudience audience, CancellationToken ct = default);

    /// <summary>The member's most recent summary for the audience, or null when none exists yet.</summary>
    Task<DigestEntry?> GetLatestAsync(
        Guid cardiMemberId, DigestAudience audience, CancellationToken ct = default);

    /// <summary>
    /// The member's summaries newest first, capped at <paramref name="limit"/> — the history
    /// behind the current one. Every filter applies <em>before</em> the cap: a search that only
    /// read the first page would answer "not found" about a review it never looked at.
    /// </summary>
    /// <param name="cardiMemberId">The member whose summaries are being read.</param>
    /// <param name="audience">Which series to read.</param>
    /// <param name="limit">Page cap, applied after the filters.</param>
    /// <param name="search">
    /// Case-insensitive text match over the summary, its headline and its suggestion; null means
    /// no text filter.
    /// </param>
    /// <param name="from">Earliest local day to include, inclusive; null means unbounded.</param>
    /// <param name="to">Latest local day to include, inclusive; null means unbounded.</param>
    /// <param name="urgency">Only entries the model graded at exactly this urgency; null means all.</param>
    /// <param name="ct">Cancels the read.</param>
    Task<IReadOnlyList<DigestEntry>> GetHistoryAsync(
        Guid cardiMemberId,
        DigestAudience audience,
        int limit,
        string? search = null,
        DateOnly? from = null,
        DateOnly? to = null,
        DigestUrgency? urgency = null,
        CancellationToken ct = default);

    /// <summary>
    /// Removes one CardiJournal book — one member, one local day, one journal audience — and
    /// returns how many rows went. The written-once indexes make that at most one for the three
    /// journal audiences. The family series is a history and is never deleted through this: a
    /// caller naming it gets an <see cref="ArgumentOutOfRangeException"/>.
    /// </summary>
    Task<int> DeleteBookAsync(
        Guid cardiMemberId, DateOnly localDate, DigestAudience audience, CancellationToken ct = default);

    /// <summary>
    /// Replaces one CardiJournal book with <paramref name="entry"/> — the delete of any earlier
    /// book for the same member, day and audience, and the insert of the new one, in one
    /// transaction. A failure between the two rolls the delete back, so the book a caregiver had
    /// is never lost to a replacement that could not be stored.
    /// </summary>
    /// <returns>How many earlier rows went, and whether the new one landed (false only when a
    /// concurrent write took the period first — see <see cref="AddAsync"/>).</returns>
    Task<(int Removed, bool Inserted)> ReplaceBookAsync(DigestEntry entry, CancellationToken ct = default);
}
