using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CardiTrack.Infrastructure.Repositories;

/// <summary>
/// Cloud SQL implementation over the partitioned digest table: raw <c>ON CONFLICT</c> upsert
/// (the natural key is the idempotency key), LINQ reads over the composite index.
/// </summary>
public class DigestRepository : IDigestRepository
{
    private readonly CardiTrackDbContext _context;

    public DigestRepository(CardiTrackDbContext context)
    {
        _context = context;
    }

    public async Task UpsertAsync(DigestEntry entry, CancellationToken ct = default)
    {
        await _context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "DigestEntries"
                ("CardiMemberId", "LocalDate", "Audience", "Text", "GeneratedAtUtc")
            VALUES ({entry.CardiMemberId}, {entry.LocalDate}, {entry.Audience.ToString()},
                    {entry.Text}, {entry.GeneratedAtUtc})
            ON CONFLICT ("CardiMemberId", "LocalDate", "Audience")
            DO UPDATE SET
                "Text" = EXCLUDED."Text",
                "GeneratedAtUtc" = EXCLUDED."GeneratedAtUtc"
            """, ct);
    }

    public async Task<DigestEntry?> GetByDateAsync(
        Guid cardiMemberId, DateOnly localDate, DigestAudience audience, CancellationToken ct = default)
    {
        return await _context.DigestEntries
            .AsNoTracking()
            .FirstOrDefaultAsync(d =>
                d.CardiMemberId == cardiMemberId
                && d.LocalDate == localDate
                && d.Audience == audience, ct);
    }

    public async Task<DigestEntry?> GetLatestAsync(
        Guid cardiMemberId, DigestAudience audience, CancellationToken ct = default)
    {
        return await _context.DigestEntries
            .AsNoTracking()
            .Where(d => d.CardiMemberId == cardiMemberId && d.Audience == audience)
            .OrderByDescending(d => d.LocalDate)
            .FirstOrDefaultAsync(ct);
    }
}
