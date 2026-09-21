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
    public async Task<Guid?> TryClaimAsync(
        Guid cardiMemberId,
        GenerationWork work,
        DateOnly periodEnd,
        DateTime utcNow,
        TimeSpan heldFor,
        CancellationToken ct = default)
    {
        var heldUntil = utcNow + heldFor;
        var claimId = Guid.NewGuid();

        // Raw SQL for the reason MemberAiHoldRepository.UpsertAsync gives, and one more: the
        // claim has to be decided by the database in a single statement, because deciding it in
        // memory is exactly the read-then-check this exists to replace. The WHERE on the DO
        // UPDATE is what makes it a claim rather than an overwrite — a live lease matches no row,
        // so the statement affects nothing and the caller learns it lost. Every mapped column is
        // listed by hand; EF cannot notice one this statement leaves out.
        //
        // "Id" is reassigned on a takeover, which is what makes the returned value an ownership
        // token rather than a row locator: the displaced holder still has the old id, so its
        // release matches nothing and cannot take the successor's claim away. Nothing references
        // this key, so rewriting it costs nothing.
        var claimed = await _context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "GenerationLeases"
                ("Id", "CardiMemberId", "Work", "PeriodEnd", "HeldUntilUtc", "ClaimedAtUtc",
                 "CreatedDate", "UpdatedDate")
            VALUES ({claimId}, {cardiMemberId}, {work.ToString()}, {periodEnd},
                    {heldUntil}, {utcNow}, {utcNow}, NULL)
            ON CONFLICT ("CardiMemberId", "Work") DO UPDATE SET
                "Id" = EXCLUDED."Id",
                "PeriodEnd" = EXCLUDED."PeriodEnd",
                "HeldUntilUtc" = EXCLUDED."HeldUntilUtc",
                "ClaimedAtUtc" = EXCLUDED."ClaimedAtUtc",
                "UpdatedDate" = EXCLUDED."ClaimedAtUtc"
            WHERE "GenerationLeases"."HeldUntilUtc" <= {utcNow}
            """, ct);

        return claimed > 0 ? claimId : null;
    }

    /// <inheritdoc />
    public async Task ReleaseAsync(Guid claimId, CancellationToken ct = default)
    {
        // By claim id, not by (member, work). A generation that overran its lease has already
        // been taken over, and deleting by the pair would remove the successor's live row and let
        // a third execution in while the second was still working. Matching nothing is the right
        // outcome for a holder that no longer holds it.
        //
        // Deleted rather than expired in place: the row's only job is to say a generation is in
        // flight, and one that has finished is saying something untrue.
        await _context.GenerationLeases
            .Where(l => l.Id == claimId)
            .ExecuteDeleteAsync(ct);
    }
}
