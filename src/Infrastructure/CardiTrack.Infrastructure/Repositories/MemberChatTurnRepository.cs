using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
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
        // and two callers racing for the same row see exactly one of them win. The second clause
        // is the supersession check: a proposal is only claimable while it is still the
        // session's latest reply, so a "yes" that read the turn before a concurrent question was
        // answered finds nothing to apply rather than applying a change the conversation has
        // moved past.
        var claimed = await _dbSet
            .Where(t => t.Id == turnId && t.PendingChange != null)
            .Where(t => !_dbSet.Any(later =>
                later.SessionId == t.SessionId
                && later.Role == ChatTurnRole.Assistant
                && later.CreatedAtUtc > t.CreatedAtUtc))
            .ExecuteUpdateAsync(set => set.SetProperty(t => t.PendingChange, (string?)null), ct);
        return claimed == 1;
    }
}
