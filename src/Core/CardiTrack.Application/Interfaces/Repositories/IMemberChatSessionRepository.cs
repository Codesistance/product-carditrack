using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Interfaces.Repositories;

/// <summary>Access to one caregiver's chat conversations about a CardiMember.</summary>
public interface IMemberChatSessionRepository : IRepository<MemberChatSession>
{
    /// <summary>
    /// The caregiver's most recently active session for this member, if one exists — what a new
    /// message continues rather than starting fresh. "Active" means not explicitly ended and
    /// last-active since the cutoff; the window itself is a client/product decision, not encoded
    /// here — the caller passes it.
    /// </summary>
    Task<MemberChatSession?> GetActiveAsync(
        Guid userId, Guid cardiMemberId, DateTime activeSinceUtc, CancellationToken ct = default);

    /// <summary>
    /// Saves a session the caller has already added, as the caregiver's one open session for the
    /// member — or reports that another request opened one first.
    /// </summary>
    /// <remarks>
    /// At most one session per caregiver and member is open (<c>EndedAtUtc</c> null), enforced by
    /// a unique index. A conversation that went quiet past <paramref name="activeSinceUtc"/> is
    /// still marked open until something closes it, so this closes those first — ended at their
    /// last turn, which is when they actually stopped — and then saves. Only quiet ones: a session
    /// another request opened a moment ago is inside the window and must win, not be closed.
    /// On <see cref="MemberChatSessionOpenOutcome.AlreadyOpen"/> the added session is detached and
    /// nothing of it is saved; the caller re-reads the active session and continues on that.
    /// </remarks>
    Task<MemberChatSessionOpenOutcome> TryOpenAsync(
        MemberChatSession added, DateTime activeSinceUtc, CancellationToken ct = default);

    /// <summary>
    /// Reopens a tracked past session as the caregiver's one open session for its member, closing
    /// every other open one first — quiet ones at their last turn, one still inside the active
    /// window at <paramref name="utcNow"/> — and saves.
    /// </summary>
    /// <remarks>
    /// The others are closed in their own statement, ahead of the save, because the one-open-
    /// session index is checked row by row. A first message can still open a session in the gap
    /// between the two; the save then hits the index, and the close-and-save is repeated so the
    /// newcomer steps aside too — continuing a past conversation is choosing it.
    /// </remarks>
    Task ReopenAsync(
        MemberChatSession session, DateTime activeSinceUtc, DateTime utcNow, CancellationToken ct = default);

    /// <summary>The session and its turns, oldest first — what the history endpoint and each new
    /// turn's prompt-history both read.</summary>
    Task<MemberChatSession?> GetByIdWithTurnsAsync(Guid sessionId, CancellationToken ct = default);

    /// <summary>
    /// The caregiver's completed conversations about this member — every session except the one
    /// still active by <paramref name="activeSinceUtc"/>'s window — newest started first, each
    /// with the facts the history list renders (opening question, question count, stored theme),
    /// computed in SQL so listing a long history never loads whole threads.
    /// </summary>
    Task<IReadOnlyList<MemberChatSessionListing>> ListCompletedForMemberAsync(
        Guid userId, Guid cardiMemberId, DateTime activeSinceUtc, CancellationToken ct = default);

    /// <summary>
    /// The caregiver's own most recent questions about this member that a reading or acting rung
    /// answered, newest first — the chat's "pick up where you left off" chips. Scoped to
    /// <paramref name="userId"/> <em>and</em> <paramref name="cardiMemberId"/>: another
    /// caregiver's questions about the same member are theirs, never offered here.
    /// </summary>
    /// <remarks>
    /// Every session counts, the active one and completed ones alike, bounded twice — asked since
    /// <paramref name="askedSinceUtc"/>, and at most <paramref name="limit"/> questions. A question
    /// qualifies by the <see cref="MemberChatWorkflow"/> stamped on the reply that followed it in
    /// the same session, which must be one of <paramref name="answeredBy"/>; a question with no
    /// stamped reply (one written before workflows were stamped) is left out, because nothing
    /// says what it was. Filtered and projected in SQL: the caller needs one column of a few
    /// dozen turns, not whole threads. Content comes back still encrypted.
    /// </remarks>
    Task<IReadOnlyList<MemberChatAskedQuestion>> ListRecentQuestionsAsync(
        Guid userId,
        Guid cardiMemberId,
        IReadOnlyCollection<MemberChatWorkflow> answeredBy,
        DateTime askedSinceUtc,
        int limit,
        CancellationToken ct = default);

    /// <summary>
    /// The theming job's work queue, across all caregivers and members: completed sessions (same
    /// predicate as <see cref="ListCompletedForMemberAsync"/>) that have no theme yet and at
    /// least one caregiver turn to derive one from. Newest activity first — the sessions a
    /// caregiver is most likely to be looking at get their labels first — and tracked, because
    /// the caller's whole purpose is to write the theme onto these rows.
    /// </summary>
    Task<IReadOnlyList<MemberChatSession>> ListUnthemedCompletedAsync(
        DateTime activeSinceUtc, int limit, CancellationToken ct = default);

    /// <summary>
    /// Takes the pending action <paramref name="session"/> was loaded with off the row, clearing it
    /// in the same statement, and returns it — or null when the row no longer holds that offer:
    /// another request took it first, or a newer offer has replaced it. Runs in the ambient
    /// transaction when one is open and autocommits otherwise. The tracked entity is brought into
    /// line with the row afterwards, so the turn's own save neither rewrites the cleared columns
    /// nor overwrites an offer set later.
    /// </summary>
    /// <param name="session">The session as this turn loaded it — the offer it read is the predicate.</param>
    /// <param name="confirming">
    /// True for a yes: a row another request holds is skipped, and null means "someone else is
    /// carrying this out". False for a no or any other message that merely spends the offer: the
    /// claim waits for a competing turn's short save to finish, because skipping there would leave
    /// the offer standing for a later yes the intervening message was meant to cancel.
    /// </param>
    /// <param name="ct">Cancels the claim.</param>
    Task<PendingChatAction?> TryConsumePendingActionAsync(
        MemberChatSession session, bool confirming, CancellationToken ct = default);

    /// <summary>
    /// Puts an offer on the session — if the row still holds the offer <paramref name="session"/>
    /// was loaded with (usually none). Two turns that both loaded a clear session and both want to
    /// offer cannot both succeed: the second is told so and leaves the first offer standing, so the
    /// row always holds the offer the caregiver was last shown. Runs in the ambient transaction
    /// when one is open — the chat opens the turn's transaction first, so the offer lands with the
    /// reply that shows it or is rolled back with it — and the tracked entity is brought into line
    /// so the turn's save neither repeats the write nor undoes a competitor's. A session not yet
    /// inserted is simply set; nothing else can hold its row.
    /// </summary>
    Task<bool> TryOfferPendingActionAsync(
        MemberChatSession session, string action, DateTime expiresAtUtc, CancellationToken ct = default);
}

/// <summary>What <see cref="IMemberChatSessionRepository.TryConsumePendingActionAsync"/> took off
/// the session: the stored line and when it stopped being honoured.</summary>
public sealed record PendingChatAction(string Action, DateTime? ExpiresAtUtc);

/// <summary>One row of <see cref="IMemberChatSessionRepository.ListCompletedForMemberAsync"/>.</summary>
public sealed record MemberChatSessionListing
{
    public required MemberChatSession Session { get; init; }

    /// <summary>The first caregiver turn's content as stored — still encrypted; the service
    /// decrypts. Null on a session that never got a caregiver turn.</summary>
    public required string? FirstQuestionContent { get; init; }

    /// <summary>Caregiver turns only — "3 questions" is what the list says, and counting the
    /// replies would double it.</summary>
    public required int QuestionCount { get; init; }
}

/// <summary>One row of <see cref="IMemberChatSessionRepository.ListRecentQuestionsAsync"/>.</summary>
public sealed record MemberChatAskedQuestion
{
    /// <summary>The caregiver turn's content as stored — still encrypted; the service decrypts.</summary>
    public required string Content { get; init; }

    public required DateTime AskedAtUtc { get; init; }
}

/// <summary>What <see cref="IMemberChatSessionRepository.TryOpenAsync"/> did with the session it was given.</summary>
public enum MemberChatSessionOpenOutcome
{
    /// <summary>Saved: it is now the caregiver's open session for the member.</summary>
    Opened = 0,

    /// <summary>Another request opened one first; this one was not saved.</summary>
    AlreadyOpen = 1,
}
