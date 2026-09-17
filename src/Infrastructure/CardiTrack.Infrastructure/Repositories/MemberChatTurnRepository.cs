using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Entities;
using CardiTrack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CardiTrack.Infrastructure.Repositories;

public class MemberChatTurnRepository : Repository<MemberChatTurn>, IMemberChatTurnRepository
{
    public MemberChatTurnRepository(CardiTrackDbContext context) : base(context)
    {
    }

    public async Task<bool> TryClaimPendingChangeAsync(Guid turnId, CancellationToken ct = default)
    {
        // ExecuteUpdate runs as one statement against the database — the WHERE is the claim,
        // and two callers racing for the same row see exactly one of them win.
        var claimed = await _dbSet
            .Where(t => t.Id == turnId && t.PendingChange != null)
            .ExecuteUpdateAsync(set => set.SetProperty(t => t.PendingChange, (string?)null), ct);
        return claimed == 1;
    }
}
