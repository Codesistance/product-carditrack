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

    public async Task UpsertAsync(RealtimeAssessment assessment, CancellationToken ct = default)
    {
        await _context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "RealtimeAssessments"
                ("CardiMemberId", "WindowStartUtc", "WindowEndUtc", "HrTrendLast",
                 "HrDeviationScore", "HrNoiseRms", "StepsSum", "SpO2Mean", "ModelOutput",
                 "RawSeverity", "Severity", "GeneratedAtUtc")
            VALUES ({assessment.CardiMemberId}, {assessment.WindowStartUtc}, {assessment.WindowEndUtc},
                    {assessment.HrTrendLast}, {assessment.HrDeviationScore}, {assessment.HrNoiseRms},
                    {assessment.StepsSum}, {assessment.SpO2Mean}, {assessment.ModelOutput},
                    {assessment.RawSeverity}, {assessment.Severity?.ToString()}, {assessment.GeneratedAtUtc})
            ON CONFLICT ("CardiMemberId", "WindowStartUtc")
            DO UPDATE SET
                "WindowEndUtc" = EXCLUDED."WindowEndUtc",
                "HrTrendLast" = EXCLUDED."HrTrendLast",
                "HrDeviationScore" = EXCLUDED."HrDeviationScore",
                "HrNoiseRms" = EXCLUDED."HrNoiseRms",
                "StepsSum" = EXCLUDED."StepsSum",
                "SpO2Mean" = EXCLUDED."SpO2Mean",
                "ModelOutput" = EXCLUDED."ModelOutput",
                "RawSeverity" = EXCLUDED."RawSeverity",
                "Severity" = EXCLUDED."Severity",
                "GeneratedAtUtc" = EXCLUDED."GeneratedAtUtc"
            """, ct);
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
