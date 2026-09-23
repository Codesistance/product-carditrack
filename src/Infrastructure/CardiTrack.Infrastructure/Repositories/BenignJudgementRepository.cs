using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Entities;
using CardiTrack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CardiTrack.Infrastructure.Repositories;

public class BenignJudgementRepository : Repository<BenignJudgement>, IBenignJudgementRepository
{
    public BenignJudgementRepository(CardiTrackDbContext context) : base(context)
    {
    }

    public async Task<IReadOnlyCollection<string>> GetJudgedFingerprintsAsync(
        Guid cardiMemberId, IReadOnlyCollection<DateOnly> localDates, CancellationToken ct = default)
    {
        if (localDates.Count == 0)
            return [];

        var dates = localDates.Distinct().ToList();
        return await _dbSet
            .Where(j => j.CardiMemberId == cardiMemberId && dates.Contains(j.LocalDate))
            .Select(j => j.FindingFingerprint)
            .ToListAsync(ct);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Written as an upsert in SQL rather than read-then-insert. The read-then-insert shape has a
    /// window between the two halves, and two assessor executions overlapping inside it is exactly
    /// the case this table has to survive — the pass is deliberately claim-free, so overlap is
    /// expected rather than exceptional. <c>ON CONFLICT DO NOTHING</c> against the unique index
    /// makes the loser a no-op instead of a <c>DbUpdateException</c> surfacing as a failed member.
    /// </remarks>
    public async Task RecordAsync(BenignJudgement judgement, CancellationToken ct = default) =>
        await _context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO "BenignJudgements" ("Id", "CardiMemberId", "Rule", "LocalDate", "FindingFingerprint", "JudgedAtUtc", "CreatedDate")
            VALUES ({Guid.NewGuid()}, {judgement.CardiMemberId}, {judgement.Rule}, {judgement.LocalDate}, {judgement.FindingFingerprint}, {judgement.JudgedAtUtc}, NOW())
            ON CONFLICT ("CardiMemberId", "Rule", "LocalDate", "FindingFingerprint") DO NOTHING
            """,
            ct);

    public async Task<int> DeleteOlderThanAsync(DateTime before, CancellationToken ct = default) =>
        await _dbSet.Where(j => j.JudgedAtUtc < before).ExecuteDeleteAsync(ct);
}
