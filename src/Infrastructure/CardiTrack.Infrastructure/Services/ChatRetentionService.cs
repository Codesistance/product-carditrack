using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CardiTrack.Infrastructure.Services;

/// <inheritdoc cref="IChatRetentionService"/>
/// <remarks>
/// Written against the <see cref="DbContext"/> rather than the chat repositories, the same
/// deliberate exception <see cref="MemberErasureService"/> makes and for the same reason: these
/// are set-based deletes over three tables whose correctness is their order, and loading a
/// quarter's conversations into memory to mark them deleted is exactly what must not happen.
/// </remarks>
public class ChatRetentionService : IChatRetentionService
{
    private readonly CardiTrackDbContext _db;

    public ChatRetentionService(CardiTrackDbContext db) => _db = db;

    public async Task<IReadOnlyList<Guid>> FindExpiredSessionsAsync(
        DateTime cutoffUtc, int limit, CancellationToken ct = default)
    {
        // The age is derived from the turns rather than read from MemberChatSession.LastTurnAtUtc,
        // which the write path maintains for the "continue the active conversation" lookup. A
        // denormalised column that drifts would either keep a conversation past its published
        // period or delete one a caregiver used yesterday, and only one of those is visible.
        // The correlated aggregate is affordable at a scale capped at 100 connected wearers, and
        // is the thing to revisit if that cap lifts.
        return await _db.MemberChatSessions
            .AsNoTracking()
            .Where(s => (_db.MemberChatTurns
                             .Where(t => t.SessionId == s.Id)
                             .Max(t => (DateTime?)t.CreatedAtUtc) ?? s.StartedAtUtc) < cutoffUtc)
            .OrderBy(s => s.StartedAtUtc)
            .Select(s => s.Id)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task<ChatRetentionReport> DeleteSessionsAsync(
        IReadOnlyList<Guid> sessionIds, CancellationToken ct = default)
    {
        if (sessionIds.Count == 0)
            return new ChatRetentionReport(0, 0, 0);

        var ids = sessionIds.ToList();

        // One transaction for the three tables: turns and usages cascade from the session by
        // configuration, so a partial commit here is the one shape that leaves a usage row with
        // nothing naming it.
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            var usages = await _db.MemberChatTurnUsages
                .Where(u => _db.MemberChatTurns.Any(t => t.Id == u.TurnId && ids.Contains(t.SessionId)))
                .ExecuteDeleteAsync(ct);
            var turns = await _db.MemberChatTurns
                .Where(t => ids.Contains(t.SessionId))
                .ExecuteDeleteAsync(ct);
            var sessions = await _db.MemberChatSessions
                .Where(s => ids.Contains(s.Id))
                .ExecuteDeleteAsync(ct);

            await transaction.CommitAsync(ct);
            return new ChatRetentionReport(sessions, turns, usages);
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }
}
