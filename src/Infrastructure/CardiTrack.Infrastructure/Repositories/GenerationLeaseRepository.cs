using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CardiTrack.Infrastructure.Repositories;

public class GenerationLeaseRepository : IGenerationLeaseRepository
{
    private readonly CardiTrackDbContext _context;

    public GenerationLeaseRepository(CardiTrackDbContext context)
    {
        _context = context;
    }

    /// <inheritdoc />
    public async Task<bool> TryClaimAsync(
        Guid cardiMemberId,
        GenerationWork work,
        DateOnly periodEnd,
        DateTime utcNow,
        TimeSpan heldFor,
        CancellationToken ct = default)
    {
        var heldUntil = utcNow + heldFor;

        // Raw SQL for the reason MemberAiHoldRepository.UpsertAsync gives, and one more: the
        // claim has to be decided by the database in a single statement, because deciding it in
        // memory is exactly the read-then-check this exists to replace. The WHERE on the DO
        // UPDATE is what makes it a claim rather than an overwrite — a live lease matches no row,
        // so the statement affects nothing and the caller learns it lost. Every mapped column is
        // listed by hand; EF cannot notice one this statement leaves out.
        var claimed = await _context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "GenerationLeases"
                ("Id", "CardiMemberId", "Work", "PeriodEnd", "HeldUntilUtc", "ClaimedAtUtc",
                 "CreatedDate", "UpdatedDate")
            VALUES ({Guid.NewGuid()}, {cardiMemberId}, {work.ToString()}, {periodEnd},
                    {heldUntil}, {utcNow}, {utcNow}, NULL)
            ON CONFLICT ("CardiMemberId", "Work") DO UPDATE SET
                "PeriodEnd" = EXCLUDED."PeriodEnd",
                "HeldUntilUtc" = EXCLUDED."HeldUntilUtc",
                "ClaimedAtUtc" = EXCLUDED."ClaimedAtUtc",
                "UpdatedDate" = EXCLUDED."ClaimedAtUtc"
            WHERE "GenerationLeases"."HeldUntilUtc" <= {utcNow}
            """, ct);

        return claimed > 0;
    }

    /// <inheritdoc />
    public async Task ReleaseAsync(
        Guid cardiMemberId, GenerationWork work, CancellationToken ct = default)
    {
        // Deleted rather than expired in place: the row's only job is to say a generation is in
        // flight, and one that has finished is saying something untrue. The next claim inserts a
        // fresh row, so nothing is lost by taking this one away.
        await _context.GenerationLeases
            .Where(l => l.CardiMemberId == cardiMemberId && l.Work == work)
            .ExecuteDeleteAsync(ct);
    }
}
