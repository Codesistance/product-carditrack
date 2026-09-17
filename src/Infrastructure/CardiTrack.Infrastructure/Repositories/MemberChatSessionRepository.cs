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
        // The offer this caller read is the predicate — the line *and* the moment it was made. A
        // claim for "whatever is pending" would let a yes sent to one offer carry out a newer one
        // that replaced it in between, and a claim on the line alone would still match the same
        // request offered again a minute later; the expiry is set from the clock at the offer, so
        // it tells two offers of the same thing apart.
        if (session.PendingAction is not { } expected)
            return null;
        var expectedExpiry = session.PendingActionExpiresAtUtc;

        // Claim and clear in one statement — the row is the lock. Two requests racing on the same
        // offer both send this; one gets the row back and the other gets nothing, which is what
        // makes "an offer is honoured once" true rather than hoped for. SKIP LOCKED because the
        // winning claim sits inside the confirming turn's short transaction (the claim, the store,
        // the turns, the save): the loser is told now rather than waiting on that commit, and told
        // "nothing" rather than reading a row the winner is about to clear. No model call ever
        // runs inside that transaction — the book is composed before it opens — so the wait
        // avoided is short; it is the answer, not the time, that matters. The offer is read
        // through the FOR UPDATE subselect, not through
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
                    WHERE "Id" = {session.Id}
                      AND "PendingAction" = {expected}
                      AND "PendingActionExpiresAtUtc" IS NOT DISTINCT FROM {expectedExpiry}
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

    public async Task<bool> TryOfferPendingActionAsync(
        MemberChatSession session, string action, DateTime expiresAtUtc, CancellationToken ct = default)
    {
        var entry = _context.Entry(session);
        if (entry.State is EntityState.Detached or EntityState.Added)
        {
            // No row yet: the insert carries the offer, and no other turn can have loaded it.
            session.PendingAction = action;
            session.PendingActionExpiresAtUtc = expiresAtUtc;
            return true;
        }

        // Compare-and-set against what this turn read. Two turns that both loaded a clear session
        // and both resolved a destructive ask race to here; the second finds the row no longer
        // clear and is refused, so the offer on the row is the one whose reply the caregiver saw
        // last — the first — and the second turn tells them to answer it. Without this the later
        // SaveChanges would win, and a yes could carry out an offer the caregiver never read.
        var observedAction = session.PendingAction;
        var observedExpiry = session.PendingActionExpiresAtUtc;
        var written = await _context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "MemberChatSessions"
            SET "PendingAction" = {action}, "PendingActionExpiresAtUtc" = {expiresAtUtc}
            WHERE "Id" = {session.Id}
              AND "PendingAction" IS NOT DISTINCT FROM {observedAction}
              AND "PendingActionExpiresAtUtc" IS NOT DISTINCT FROM {observedExpiry}
            """, ct);

        if (written == 0)
            return false;

        // The entity follows the row, with matching original values, so the turn's own save has
        // nothing further to write for these columns.
        session.PendingAction = action;
        session.PendingActionExpiresAtUtc = expiresAtUtc;
        foreach (var column in new[] { entry.Property(x => x.PendingAction), (PropertyEntry)entry.Property(x => x.PendingActionExpiresAtUtc) })
        {
            column.OriginalValue = column.CurrentValue;
            column.IsModified = false;
        }

        return true;
    }

    private sealed class PendingActionRow
    {
        public string Action { get; set; } = string.Empty;

        public DateTime? ExpiresAtUtc { get; set; }
    }
}
