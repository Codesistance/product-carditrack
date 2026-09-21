using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
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
    private readonly IMemberWriteGuard _guard;

    public RealtimeAssessmentRepository(CardiTrackDbContext context, IMemberWriteGuard guard)
    {
        _context = context;
        _guard = guard;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Guarded: the caller reaches here after a MedGemma call that can run for minutes, and an
    /// erasure landing inside it would otherwise leave this window's assessment — and the alert
    /// the claim below routes — describing a member who no longer exists. A refused write returns
    /// false, which the caller already reads as "not mine to route", so the assessment is not
    /// written and no alert is raised. That is the right answer rather than a convenient one: a
    /// pass that was refused has nothing to claim.
    /// </remarks>
    public async Task<bool> UpsertAsync(RealtimeAssessment assessment, CancellationToken ct = default)
    {
        var claimed = false;
        await _guard.WriteIfMemberLivesAsync(
            assessment.CardiMemberId, async inner => claimed = await WriteAsync(assessment, inner), ct);
        return claimed;
    }

    /// <summary>
    /// The upsert itself, which assumes the caller holds the member's row lock.
    /// </summary>
    private async Task<bool> WriteAsync(RealtimeAssessment assessment, CancellationToken ct)
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
                 "RawSeverity", "Severity", "SsaEngine", "GeneratedAtUtc")
            VALUES ({assessment.CardiMemberId}, {assessment.WindowStartUtc}, {assessment.WindowEndUtc},
                    {assessment.HrTrendLast}, {assessment.HrDeviationScore}, {assessment.HrNoiseRms},
                    {assessment.StepsSum}, {assessment.SpO2Mean}, {assessment.ModelOutput},
                    {assessment.RawSeverity}, {assessment.Severity?.ToString()}, {assessment.SsaEngine},
                    {assessment.GeneratedAtUtc})
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
                "SsaEngine" = {assessment.SsaEngine},
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

    public async Task<IReadOnlyList<RealtimeAssessment>> GetSinceAsync(
        Guid cardiMemberId, DateTime sinceUtc, CancellationToken ct = default)
    {
        return await _context.RealtimeAssessments
            .AsNoTracking()
            .Where(a => a.CardiMemberId == cardiMemberId && a.WindowStartUtc >= sinceUtc)
            .OrderByDescending(a => a.WindowStartUtc)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<RealtimeAssessment>> GetBetweenAsync(
        Guid cardiMemberId, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        // Both bounds on the partition column, so one day reads one or two partitions.
        return await _context.RealtimeAssessments
            .AsNoTracking()
            .Where(a => a.CardiMemberId == cardiMemberId
                        && a.WindowStartUtc >= fromUtc
                        && a.WindowStartUtc < toUtc)
            .OrderBy(a => a.WindowStartUtc)
            .ToListAsync(ct);
    }
}
