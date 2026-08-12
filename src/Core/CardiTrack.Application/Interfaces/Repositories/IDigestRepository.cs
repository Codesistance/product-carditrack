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
    Task AddAsync(DigestEntry entry, CancellationToken ct = default);

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
    /// behind the current one.
    /// </summary>
    Task<IReadOnlyList<DigestEntry>> GetHistoryAsync(
        Guid cardiMemberId, DigestAudience audience, int limit, CancellationToken ct = default);
}
