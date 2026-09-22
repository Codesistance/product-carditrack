using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CardiTrack.Infrastructure.Services;

/// <inheritdoc cref="IFamilyWriteGuard"/>
public class FamilyWriteGuard : IFamilyWriteGuard
{
    private readonly CardiTrackDbContext _context;

    public FamilyWriteGuard(CardiTrackDbContext context)
    {
        _context = context;
    }

    public async Task<bool> HoldAsync(Guid organizationId, CancellationToken ct = default)
    {
        // FOR UPDATE, not FOR KEY SHARE. The member guard wants the weakest lock that still
        // conflicts with an erasure, because two generators writing about one member should not
        // wait on each other. Here the opposite is true: two callers changing the same family's
        // shape are exactly what must not proceed together, so they queue.
        var live = await _context.Database.SqlQuery<int>($"""
            SELECT 1 AS "Value" FROM "Organizations" WHERE "Id" = {organizationId} FOR UPDATE
            """).ToListAsync(ct);

        return live.Count > 0;
    }

    public async Task<bool> HoldAccountAsync(Guid userId, CancellationToken ct = default)
    {
        var live = await _context.Database.SqlQuery<int>($"""
            SELECT 1 AS "Value" FROM "Users" WHERE "Id" = {userId} FOR UPDATE
            """).ToListAsync(ct);

        return live.Count > 0;
    }
}
