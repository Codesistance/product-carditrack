using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Entities;
using CardiTrack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CardiTrack.Infrastructure.Repositories;

public class MemberChatTurnUsageRepository : Repository<MemberChatTurnUsage>, IMemberChatTurnUsageRepository
{
    public MemberChatTurnUsageRepository(CardiTrackDbContext context) : base(context)
    {
    }

    public async Task<IReadOnlyList<MemberChatTurnUsage>> GetByTurnIdsAsync(
        IReadOnlyCollection<Guid> turnIds, CancellationToken ct = default)
    {
        return await _dbSet
            .AsNoTracking()
            .Where(u => turnIds.Contains(u.TurnId))
            .ToListAsync(ct);
    }
}
