using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Interfaces.Repositories;

/// <summary>
/// Read/write surface over the partitioned digest table. Not an <see cref="IRepository{T}"/> —
/// composite-keyed and partitioned, like the granular tables.
/// </summary>
public interface IDigestRepository
{
    /// <summary>Idempotently stores a digest — a regenerated day replaces its earlier self.</summary>
    Task UpsertAsync(DigestEntry entry, CancellationToken ct = default);

    /// <summary>The digest for one member's local day, or null when none was generated.</summary>
    Task<DigestEntry?> GetByDateAsync(
        Guid cardiMemberId, DateOnly localDate, DigestAudience audience, CancellationToken ct = default);

    /// <summary>The member's most recent digest for the audience, or null when none exists yet.</summary>
    Task<DigestEntry?> GetLatestAsync(
        Guid cardiMemberId, DigestAudience audience, CancellationToken ct = default);
}
