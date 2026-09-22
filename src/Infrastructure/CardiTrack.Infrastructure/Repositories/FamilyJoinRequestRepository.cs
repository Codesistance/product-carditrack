using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CardiTrack.Infrastructure.Repositories;

public class FamilyJoinRequestRepository : Repository<FamilyJoinRequest>, IFamilyJoinRequestRepository
{
    public FamilyJoinRequestRepository(CardiTrackDbContext context) : base(context)
    {
    }

    public async Task<IReadOnlyList<FamilyJoinRequest>> GetPendingForOrganizationAsync(
        Guid organizationId, DateTime utcNow, CancellationToken ct = default)
    {
        // Expiry is filtered here rather than swept: an expired request is not waiting on anybody,
        // and an admin's queue should not show work that can no longer be done.
        return await _dbSet
            .AsNoTracking()
            .Where(r => r.OrganizationId == organizationId
                && r.Status == FamilyJoinRequestStatus.Pending
                && r.ExpiresAt > utcNow)
            .OrderBy(r => r.CreatedDate)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<FamilyJoinRequest>> GetForUserAsync(
        Guid userId, CancellationToken ct = default)
    {
        return await _dbSet
            .AsNoTracking()
            .Where(r => r.RequestedByUserId == userId)
            .OrderByDescending(r => r.CreatedDate)
            .ToListAsync(ct);
    }

    public async Task<FamilyJoinRequest> AddOrGetLiveAsync(
        FamilyJoinRequest request, DateTime utcNow, CancellationToken ct = default)
    {
        await _dbSet.AddAsync(request, ct);

        try
        {
            await _context.SaveChangesAsync(ct);
            return request;
        }
        catch (DbUpdateException)
        {
            // Lost the insert race. Drop this attempt from the change tracker first — left
            // Added, the next SaveChangesAsync on this scope would retry it and fail again,
            // taking an unrelated write down with it.
            _context.Entry(request).State = EntityState.Detached;

            var winner = await GetLiveAsync(
                request.RequestedByUserId, request.OrganizationId, utcNow, ct);

            // Nothing live after a conflict means the conflict was not the one this handles —
            // let it go up rather than swallow a real failure.
            if (winner is null)
                throw;

            return winner;
        }
    }

    public async Task<FamilyJoinRequest?> GetLiveAsync(
        Guid userId, Guid organizationId, DateTime utcNow, CancellationToken ct = default)
    {
        return await _dbSet.FirstOrDefaultAsync(
            r => r.RequestedByUserId == userId
                && r.OrganizationId == organizationId
                && r.Status == FamilyJoinRequestStatus.Pending
                && r.ExpiresAt > utcNow,
            ct);
    }

    public async Task<bool> TryResolveAsync(
        Guid requestId,
        FamilyJoinRequestStatus to,
        Guid? resolvedByUserId,
        DateTime resolvedAt,
        CancellationToken ct = default)
    {
        var moved = await _dbSet
            .Where(r => r.Id == requestId && r.Status == FamilyJoinRequestStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, to)
                .SetProperty(r => r.ResolvedByUserId, resolvedByUserId)
                .SetProperty(r => r.ResolvedAt, resolvedAt)
                .SetProperty(r => r.UpdatedDate, resolvedAt), ct);

        return moved > 0;
    }
}
