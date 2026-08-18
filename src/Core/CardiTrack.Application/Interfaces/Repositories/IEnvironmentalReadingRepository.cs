using CardiTrack.Domain.Entities;

namespace CardiTrack.Application.Interfaces.Repositories;

/// <summary>
/// Access to the day-partitioned environmental-reading table. Writes are upserts on the natural
/// key (CardiMemberId, SessionStartUtc) — re-running an enrich pass over the same session must
/// overwrite, never duplicate.
/// </summary>
public interface IEnvironmentalReadingRepository
{
    /// <summary>
    /// Writes the reading; true when this call inserted the row, false when it overwrote an
    /// existing one.
    /// </summary>
    Task<bool> UpsertAsync(EnvironmentalReading reading, CancellationToken ct = default);

    /// <summary>Whether a reading already exists for this exact session — the dedup probe that
    /// keeps an already-enriched session from ever calling the environmental client twice.</summary>
    Task<bool> ExistsAsync(Guid cardiMemberId, DateTime sessionStartUtc, CancellationToken ct = default);

    /// <summary>The member's most recent reading by session start, or null — feeds the
    /// assessment/trend prompts' environmental context line.</summary>
    Task<EnvironmentalReading?> GetLatestAsync(Guid cardiMemberId, CancellationToken ct = default);

    /// <summary>
    /// Every reading whose session overlaps [<paramref name="fromUtc"/>, <paramref name="toUtc"/>),
    /// oldest first — the daybook's read: the conditions of one finished local day, not the latest
    /// session wherever it happened to fall. Callers gate on the member's environmental consent
    /// exactly as <c>EnvironmentalContextSource</c> does; this method does not re-check it.
    /// </summary>
    Task<IReadOnlyList<EnvironmentalReading>> GetOverlappingAsync(
        Guid cardiMemberId, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);
}
