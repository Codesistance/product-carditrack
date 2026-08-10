using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Entities;
using CardiTrack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CardiTrack.Infrastructure.Repositories;

/// <summary>
/// Cloud SQL implementation over the partitioned assessment table: raw <c>ON CONFLICT</c>
/// upsert (the natural key is the idempotency key), LINQ reads over the composite index.
/// </summary>
public class RealtimeAssessmentRepository : IRealtimeAssessmentRepository
{
    private readonly CardiTrackDbContext _context;

    public RealtimeAssessmentRepository(CardiTrackDbContext context)
    {
        _context = context;
    }

    public async Task<bool> UpsertAsync(RealtimeAssessment assessment, CancellationToken ct = default)
    {
        // Claim-then-update rather than a single DO UPDATE: the caller treats "I inserted" as
        // an exclusive claim on the window (only the inserter routes an alert), and the claim
        // must be decided atomically in the statement itself. (`RETURNING (xmax = 0)` would say
        // the same thing in one statement, but PostgreSQL refuses system columns in RETURNING
        // on partitioned tables.) A row returned means this call created the row; conflicting
        // callers fall through to a plain update — the row cannot vanish in between, because
        // nothing deletes assessments except a partition drop months later.
        var claimed = await _context.Database.SqlQuery<int>($"""
            INSERT INTO "RealtimeAssessments"
                ("CardiMemberId", "WindowStartUtc", "WindowEndUtc", "HrTrendLast",
                 "HrDeviationScore", "HrNoiseRms", "StepsSum", "SpO2Mean", "ModelOutput",
                 "RawSeverity", "Severity", "GeneratedAtUtc")
            VALUES ({assessment.CardiMemberId}, {assessment.WindowStartUtc}, {assessment.WindowEndUtc},
                    {assessment.HrTrendLast}, {assessment.HrDeviationScore}, {assessment.HrNoiseRms},
                    {assessment.StepsSum}, {assessment.SpO2Mean}, {assessment.ModelOutput},
                    {assessment.RawSeverity}, {assessment.Severity?.ToString()}, {assessment.GeneratedAtUtc})
            ON CONFLICT ("CardiMemberId", "WindowStartUtc") DO NOTHING
            RETURNING 1 AS "Value"
            """).ToListAsync(ct);

        if (claimed.Count > 0)
            return true;

        await _context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "RealtimeAssessments" SET
                "WindowEndUtc" = {assessment.WindowEndUtc},
                "HrTrendLast" = {assessment.HrTrendLast},
                "HrDeviationScore" = {assessment.HrDeviationScore},
                "HrNoiseRms" = {assessment.HrNoiseRms},
                "StepsSum" = {assessment.StepsSum},
                "SpO2Mean" = {assessment.SpO2Mean},
                "ModelOutput" = {assessment.ModelOutput},
                "RawSeverity" = {assessment.RawSeverity},
                "Severity" = {assessment.Severity?.ToString()},
                "GeneratedAtUtc" = {assessment.GeneratedAtUtc}
            WHERE "CardiMemberId" = {assessment.CardiMemberId}
              AND "WindowStartUtc" = {assessment.WindowStartUtc}
            """, ct);
        return false;
    }

    public async Task<bool> ExistsAsync(
        Guid cardiMemberId, DateTime windowStartUtc, CancellationToken ct = default)
    {
        return await _context.RealtimeAssessments
            .AsNoTracking()
            .AnyAsync(a => a.CardiMemberId == cardiMemberId && a.WindowStartUtc == windowStartUtc, ct);
    }

    public async Task<RealtimeAssessment?> GetLatestAsync(
        Guid cardiMemberId, CancellationToken ct = default)
    {
        return await _context.RealtimeAssessments
            .AsNoTracking()
            .Where(a => a.CardiMemberId == cardiMemberId)
            .OrderByDescending(a => a.WindowStartUtc)
            .FirstOrDefaultAsync(ct);
    }
}
