using CardiTrack.Domain.Entities;

namespace CardiTrack.Application.Interfaces.Repositories;

/// <summary>
/// Access to the day-partitioned rhythm-episode table. Writes are upserts on the natural key
/// (CardiMemberId, WindowStartUtc): the routine sync re-reads the last three days on every pull,
/// so the same analysis window arrives repeatedly and must land once.
/// </summary>
public interface IRhythmEpisodeRepository
{
    /// <summary>
    /// Writes the episode; true when this call inserted the row, false when it overwrote an
    /// existing one. Overwriting rather than skipping because a provider may serve a window again
    /// with more beats than it first had.
    /// </summary>
    Task<bool> UpsertAsync(RhythmEpisode episode, CancellationToken ct = default);

    /// <summary>
    /// Every episode whose window starts in [<paramref name="fromUtc"/>, <paramref name="toUtc"/>),
    /// oldest first — the alert detail's read for one notification, and the report's for a range.
    /// </summary>
    Task<IReadOnlyList<RhythmEpisode>> GetInRangeAsync(
        Guid cardiMemberId, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);
}
