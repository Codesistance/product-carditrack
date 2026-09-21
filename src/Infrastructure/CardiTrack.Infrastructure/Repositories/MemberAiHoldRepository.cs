using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CardiTrack.Infrastructure.Repositories;

public class MemberAiHoldRepository : IMemberAiHoldRepository
{
    private readonly CardiTrackDbContext _context;
    private readonly IMemberWriteGuard _guard;

    public MemberAiHoldRepository(CardiTrackDbContext context, IMemberWriteGuard guard)
    {
        _context = context;
        _guard = guard;
    }

    public async Task<MemberAiHold?> GetAsync(
        Guid cardiMemberId, AiHoldPurpose purpose, CancellationToken ct = default)
    {
        return await _context.MemberAiHolds
            .AsNoTracking()
            .FirstOrDefaultAsync(h => h.CardiMemberId == cardiMemberId && h.Purpose == purpose, ct);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Guarded: the hold is written after a model call that did not finish, so it lands minutes
    /// after the member was read, exactly as the digest it was standing in for would have. A hold
    /// row describes a member as much as a digest does — it says this person's summary is being
    /// suppressed — and an erased member has nothing left to suppress.
    /// </remarks>
    public async Task UpsertAsync(MemberAiHold hold, CancellationToken ct = default)
    {
        await _guard.WriteIfMemberLivesAsync(
            hold.CardiMemberId, inner => WriteAsync(hold, inner), ct);
    }

    private async Task WriteAsync(MemberAiHold hold, CancellationToken ct)
    {
        // Raw SQL for the same reason the digest's own insert is: two pipeline executions can
        // overlap on one member, and a tracked add-or-update races itself into a unique
        // violation on the second insert. ON CONFLICT lets the later of the two writes win, and
        // it executes now rather than at the caller's next SaveChangesAsync — a hold is written
        // on a failure path, where there is no digest to save alongside it. Every mapped column
        // is listed by hand; EF cannot notice one this statement leaves out.
        await _context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "MemberAiHolds"
                ("Id", "CardiMemberId", "Purpose", "HeldUntilUtc", "LastFailedAtUtc",
                 "ConsecutiveFailures", "Reason", "CreatedDate", "UpdatedDate")
            VALUES ({hold.Id}, {hold.CardiMemberId}, {hold.Purpose.ToString()}, {hold.HeldUntilUtc},
                    {hold.LastFailedAtUtc}, {hold.ConsecutiveFailures}, {hold.Reason}, {hold.CreatedDate}, NULL)
            ON CONFLICT ("CardiMemberId", "Purpose") DO UPDATE SET
                "HeldUntilUtc" = EXCLUDED."HeldUntilUtc",
                "LastFailedAtUtc" = EXCLUDED."LastFailedAtUtc",
                "ConsecutiveFailures" = EXCLUDED."ConsecutiveFailures",
                "Reason" = EXCLUDED."Reason",
                "UpdatedDate" = EXCLUDED."LastFailedAtUtc"
            """, ct);
    }

    public async Task ClearAsync(Guid cardiMemberId, AiHoldPurpose purpose, CancellationToken ct = default)
    {
        await _context.MemberAiHolds
            .Where(h => h.CardiMemberId == cardiMemberId && h.Purpose == purpose)
            .ExecuteDeleteAsync(ct);
    }
}
