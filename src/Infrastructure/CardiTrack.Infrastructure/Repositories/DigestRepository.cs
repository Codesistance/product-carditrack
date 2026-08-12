using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CardiTrack.Infrastructure.Repositories;

/// <summary>
/// Cloud SQL implementation over the partitioned summary table: raw insert (the natural key
/// carries the generation instant, so a collision means a duplicate run, not a rewrite), LINQ
/// reads over the composite key.
/// </summary>
public class DigestRepository : IDigestRepository
{
    private readonly CardiTrackDbContext _context;

    public DigestRepository(CardiTrackDbContext context)
    {
        _context = context;
    }

    public async Task AddAsync(DigestEntry entry, CancellationToken ct = default)
    {
        // DO NOTHING rather than DO UPDATE: two overlapping pipeline executions can generate for
        // the same member at the same instant, and the second has nothing to add — but an ordinary
        // recomputation carries a later GeneratedAtUtc and lands as its own row, which is what
        // makes the day's history a history.
        await _context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "DigestEntries"
                ("CardiMemberId", "LocalDate", "Audience", "Headline", "Text", "GeneratedAtUtc")
            VALUES ({entry.CardiMemberId}, {entry.LocalDate}, {entry.Audience.ToString()},
                    {entry.Headline}, {entry.Text}, {entry.GeneratedAtUtc})
            ON CONFLICT ("CardiMemberId", "LocalDate", "Audience", "GeneratedAtUtc")
            DO NOTHING
            """, ct);
    }

    public async Task<DigestEntry?> GetLatestByDateAsync(
        Guid cardiMemberId, DateOnly localDate, DigestAudience audience, CancellationToken ct = default)
    {
        return await _context.DigestEntries
            .AsNoTracking()
            .Where(d =>
                d.CardiMemberId == cardiMemberId
                && d.LocalDate == localDate
                && d.Audience == audience)
            .OrderByDescending(d => d.GeneratedAtUtc)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<DigestEntry?> GetLatestAsync(
        Guid cardiMemberId, DigestAudience audience, CancellationToken ct = default)
    {
        return await _context.DigestEntries
            .AsNoTracking()
            .Where(d => d.CardiMemberId == cardiMemberId && d.Audience == audience)
            .OrderByDescending(d => d.LocalDate)
            .ThenByDescending(d => d.GeneratedAtUtc)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<DigestEntry>> GetHistoryAsync(
        Guid cardiMemberId, DigestAudience audience, int limit, CancellationToken ct = default)
    {
        return await _context.DigestEntries
            .AsNoTracking()
            .Where(d => d.CardiMemberId == cardiMemberId && d.Audience == audience)
            .OrderByDescending(d => d.LocalDate)
            .ThenByDescending(d => d.GeneratedAtUtc)
            .Take(limit)
            .ToListAsync(ct);
    }
}
