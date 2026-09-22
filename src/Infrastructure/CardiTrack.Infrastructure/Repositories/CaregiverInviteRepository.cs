using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CardiTrack.Infrastructure.Repositories;

public class CaregiverInviteRepository : Repository<CaregiverInvite>, ICaregiverInviteRepository
{
    public CaregiverInviteRepository(CardiTrackDbContext context) : base(context)
    {
    }

    public async Task<CaregiverInvite?> GetByTokenHashAsync(string tokenHash, CancellationToken ct = default)
    {
        return await _dbSet.FirstOrDefaultAsync(i => i.TokenHash == tokenHash, ct);
    }

    public async Task<IReadOnlyList<CaregiverInvite>> GetForMemberAsync(
        Guid cardiMemberId, CancellationToken ct = default)
    {
        return await _dbSet
            .AsNoTracking()
            .Where(i => i.CardiMemberId == cardiMemberId)
            .OrderByDescending(i => i.CreatedDate)
            .ToListAsync(ct);
    }

    public async Task<bool> TryResolveAsync(
        Guid inviteId,
        IReadOnlyCollection<CaregiverInviteStatus> from,
        CaregiverInviteStatus to,
        DateTime resolvedAt,
        Guid? acceptedByUserId,
        CancellationToken ct = default)
    {
        // The status precondition rides in the WHERE clause so two racing callers cannot both
        // believe they resolved it.
        var moved = await _dbSet
            .Where(i => i.Id == inviteId && from.Contains(i.Status))
            .ExecuteUpdateAsync(s => s
                .SetProperty(i => i.Status, to)
                .SetProperty(i => i.ResolvedAt, resolvedAt)
                .SetProperty(i => i.AcceptedByUserId, acceptedByUserId)
                .SetProperty(i => i.UpdatedDate, resolvedAt), ct);

        return moved > 0;
    }

    public async Task<bool> TryMarkOpenedAsync(Guid inviteId, DateTime openedAt, CancellationToken ct = default)
    {
        var moved = await _dbSet
            .Where(i => i.Id == inviteId && i.Status == CaregiverInviteStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(i => i.Status, CaregiverInviteStatus.Opened)
                .SetProperty(i => i.OpenedAt, openedAt)
                .SetProperty(i => i.UpdatedDate, openedAt), ct);

        return moved > 0;
    }
}
