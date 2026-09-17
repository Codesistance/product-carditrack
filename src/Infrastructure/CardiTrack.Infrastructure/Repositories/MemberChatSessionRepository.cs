using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace CardiTrack.Infrastructure.Repositories;

public class MemberChatSessionRepository : Repository<MemberChatSession>, IMemberChatSessionRepository
{
    public MemberChatSessionRepository(CardiTrackDbContext context) : base(context)
    {
    }

    public async Task<MemberChatSession?> GetActiveAsync(
        Guid userId, Guid cardiMemberId, DateTime activeSinceUtc, CancellationToken ct = default)
    {
        // Tracked, not AsNoTracking: the caller's next move is almost always to append a turn and
        // bump LastTurnAtUtc on this same instance.
        return await _dbSet
            .Where(s => s.UserId == userId
                        && s.CardiMemberId == cardiMemberId
                        && s.EndedAtUtc == null
                        && s.LastTurnAtUtc >= activeSinceUtc)
            .OrderByDescending(s => s.LastTurnAtUtc)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<MemberChatSession?> GetByIdWithTurnsAsync(Guid sessionId, CancellationToken ct = default)
    {
        return await _dbSet
            .AsNoTracking()
            .Include(s => s.Turns.OrderBy(t => t.CreatedAtUtc))
            .FirstOrDefaultAsync(s => s.Id == sessionId, ct);
    }

    public async Task<IReadOnlyList<MemberChatSessionListing>> ListCompletedForMemberAsync(
        Guid userId, Guid cardiMemberId, DateTime activeSinceUtc, CancellationToken ct = default)
    {
        // Projected, not Included: the list needs one turn's content and a count per session, and
        // Include would drag every encrypted turn of every conversation across the wire to show a
        // one-line summary of each. "Completed" is the complement of GetActiveAsync's predicate:
        // explicitly ended, or quiet past the active window — never the conversation the chat
        // window is still having.
        return await _dbSet
            .AsNoTracking()
            .Where(s => s.UserId == userId
                        && s.CardiMemberId == cardiMemberId
                        && (s.EndedAtUtc != null || s.LastTurnAtUtc < activeSinceUtc))
            .OrderByDescending(s => s.StartedAtUtc)
            .Select(s => new MemberChatSessionListing
            {
                Session = s,
                FirstQuestionContent = s.Turns
                    .Where(t => t.Role == ChatTurnRole.User)
                    .OrderBy(t => t.CreatedAtUtc)
                    .Select(t => t.Content)
                    .FirstOrDefault(),
                QuestionCount = s.Turns.Count(t => t.Role == ChatTurnRole.User),
            })
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<MemberChatSession>> ListUnthemedCompletedAsync(
        DateTime activeSinceUtc, int limit, CancellationToken ct = default)
    {
        // Tracked, not AsNoTracking: the theming job's next move is to write Theme onto these
        // rows. The user-turn requirement mirrors the history list's own rule — a session with
        // no caregiver question never becomes a row there, so labelling it buys nothing.
        // "Newest activity" counts the explicit end as activity: an ended conversation's most
        // recent moment is the ending, not its last message.
        return await _dbSet
            .Where(s => s.Theme == null
                        && (s.EndedAtUtc != null || s.LastTurnAtUtc < activeSinceUtc)
                        && s.Turns.Any(t => t.Role == ChatTurnRole.User))
            .OrderByDescending(s => s.EndedAtUtc ?? s.LastTurnAtUtc)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task<PendingChatAction?> TryConsumePendingActionAsync(
        MemberChatSession session, CancellationToken ct = default)
    {
        // The offer this caller read is part of the predicate: a claim for "whatever is pending"
        // would let a yes sent to one offer carry out a newer one that replaced it in between.
        if (session.PendingAction is not { } expected)
            return null;

        // Claim and clear in one statement — the row is the lock. Two requests racing on the same
        // offer both send this; one gets the row back and the other gets nothing, which is what
        // makes "an offer is honoured once" true rather than hoped for. SKIP LOCKED because the
        // winning claim may sit inside a transaction that lasts a MedGemma call: the loser is told
        // now, not after that call. The offer is read through the FOR UPDATE subselect, not through
        // the updated row: RETURNING on an UPDATE yields the row *after* the update — the nulls
        // just written — while a FROM-list table's columns keep their pre-update values. Written as
        // a CTE so the query stays legal if EF wraps it in a subquery.
        var rows = await _context.Database.SqlQuery<PendingActionRow>($"""
            WITH claimed AS (
                UPDATE "MemberChatSessions" AS s
                SET "PendingAction" = NULL, "PendingActionExpiresAtUtc" = NULL
                FROM (
                    SELECT "Id", "PendingAction", "PendingActionExpiresAtUtc"
                    FROM "MemberChatSessions"
                    WHERE "Id" = {session.Id} AND "PendingAction" = {expected}
                    FOR UPDATE SKIP LOCKED) AS before
                WHERE s."Id" = before."Id"
                RETURNING before."PendingAction" AS "Action", before."PendingActionExpiresAtUtc" AS "ExpiresAtUtc")
            SELECT "Action", "ExpiresAtUtc"
            FROM claimed
            """).ToListAsync(ct);

        // The entity follows the row: cleared, and with cleared *original* values, so the turn's
        // save sees nothing to write for these columns — unless a handler sets a new offer later
        // in the same turn, which then differs from the original and is written as it should be.
        // Done whether or not the claim succeeded: on a miss the row holds a newer offer this
        // entity never saw, and the one thing the save must not do is overwrite it with nulls.
        session.PendingAction = null;
        session.PendingActionExpiresAtUtc = null;
        var entry = _context.Entry(session);
        if (entry.State is not (EntityState.Detached or EntityState.Added))
        {
            // Both halves, deliberately: the original value so the next change detection has
            // nothing to compare against, and the modified flag because setting the current value
            // above already raised it — and a raised flag writes the column whatever the snapshot
            // says (the integration test for a replaced offer is what caught that).
            foreach (var column in new[] { entry.Property(x => x.PendingAction), (PropertyEntry)entry.Property(x => x.PendingActionExpiresAtUtc) })
            {
                column.OriginalValue = null;
                column.IsModified = false;
            }
        }

        var row = rows.SingleOrDefault();
        return row is null ? null : new PendingChatAction(row.Action, row.ExpiresAtUtc);
    }

    private sealed class PendingActionRow
    {
        public string Action { get; set; } = string.Empty;

        public DateTime? ExpiresAtUtc { get; set; }
    }
}
