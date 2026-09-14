using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Domain.Entities;
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
        // Ordered by the same value the predicate filters on, not by StartedAtUtc. They are not
        // the same ordering: a conversation started two years ago but answered last quarter is
        // younger, by the rule that decides this, than one started last month and abandoned the
        // same week. Ordering by the start date would let the second wait behind the first
        // indefinitely whenever the backlog is larger than one batch.
        return await Expired(cutoffUtc)
            .OrderBy(s => _db.MemberChatTurns
                .Where(t => t.SessionId == s.Id)
                .Max(t => (DateTime?)t.CreatedAtUtc) ?? s.StartedAtUtc)
            .Select(s => s.Id)
            .Take(limit)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Conversations whose age has passed <paramref name="cutoffUtc"/> — the newest turn, or when
    /// the session was started if it never got one.
    /// </summary>
    /// <remarks>
    /// The age is derived from the turns rather than read from
    /// <c>MemberChatSession.LastTurnAtUtc</c>, which the write path maintains for the "continue
    /// the active conversation" lookup and bumps when a past conversation is merely reopened. A
    /// denormalised column that drifts would either keep a conversation past its published period
    /// or delete one a caregiver used yesterday, and only one of those is visible. The correlated
    /// aggregate is affordable at a scale capped at 100 connected wearers, and is the thing to
    /// revisit if that cap lifts.
    ///
    /// One definition, used by the find and again by the delete, so the two cannot drift into
    /// disagreeing about what "expired" means.
    /// </remarks>
    private IQueryable<MemberChatSession> Expired(DateTime cutoffUtc) =>
        _db.MemberChatSessions
            .AsNoTracking()
            .Where(s => (_db.MemberChatTurns
                             .Where(t => t.SessionId == s.Id)
                             .Max(t => (DateTime?)t.CreatedAtUtc) ?? s.StartedAtUtc) < cutoffUtc);

    public async Task<ChatRetentionReport> DeleteSessionsAsync(
        IReadOnlyList<Guid> sessionIds, DateTime cutoffUtc, CancellationToken ct = default)
    {
        if (sessionIds.Count == 0)
            return new ChatRetentionReport(0, 0, 0);

        var candidates = sessionIds.ToList();

        // One transaction for the three tables: turns and usages cascade from the session by
        // configuration, so a partial commit here is the one shape that leaves a usage row with
        // nothing naming it.
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            // Re-asked inside the transaction, against the same rule, because the ids were
            // chosen by an earlier read. The advisory lock keeps a second worker out; it does
            // not keep a caregiver out, and a reply written in that gap makes the conversation
            // live again. Narrowing the list is cheap; taking a thread somebody is in the middle
            // of is not undoable.
            var ids = await Expired(cutoffUtc)
                .Where(s => candidates.Contains(s.Id))
                .Select(s => s.Id)
                .ToListAsync(ct);

            if (ids.Count == 0)
            {
                await transaction.CommitAsync(ct);
                return new ChatRetentionReport(0, 0, 0);
            }

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
