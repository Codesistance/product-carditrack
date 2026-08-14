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
        // Every mapped column is listed explicitly, and a new one has to be added here by hand —
        // this insert is raw SQL, so EF cannot notice a property it does not mention. "Suggestions"
        // was added to the entity, the configuration and a migration without reaching this list,
        // and the result was silent: the generator validated its suggestions and assigned them,
        // the row inserted cleanly with the column left NULL, and the apps hid a section they were
        // never given anything to show. No exception, no warning, nothing in a log to find.
        // Urgency.ToString() would be wrong here: Nullable<T>.ToString() returns "" (not null)
        // when unset, which would insert an empty string a later read could not parse back as
        // the enum HasConversion<string>() expects. The null-conditional keeps it a real NULL.
        await _context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "DigestEntries"
                ("CardiMemberId", "LocalDate", "Audience", "Headline", "Text", "Suggestion", "Urgency", "GeneratedAtUtc")
            VALUES ({entry.CardiMemberId}, {entry.LocalDate}, {entry.Audience.ToString()},
                    {entry.Headline}, {entry.Text}, {entry.Suggestion}, {entry.Urgency?.ToString()}, {entry.GeneratedAtUtc})
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
