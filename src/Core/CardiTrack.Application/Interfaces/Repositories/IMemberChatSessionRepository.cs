using CardiTrack.Domain.Entities;

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
    /// another request took it first, a newer offer has replaced it, or the row is locked by a
    /// claim in flight. Runs in the ambient transaction when one is open and autocommits
    /// otherwise. The tracked entity is brought into line with the row afterwards, so the turn's
    /// own save neither rewrites the cleared columns nor overwrites an offer set later.
    /// </summary>
    Task<PendingChatAction?> TryConsumePendingActionAsync(MemberChatSession session, CancellationToken ct = default);

    /// <summary>
    /// Puts an offer on the session — if the row still holds the offer <paramref name="session"/>
    /// was loaded with (usually none). Two turns that both loaded a clear session and both want to
    /// offer cannot both succeed: the second is told so and leaves the first offer standing, so the
    /// row always holds the offer the caregiver was last shown. Autocommitted, and the tracked
    /// entity is brought into line so the turn's save neither repeats the write nor undoes a
    /// competitor's. A session not yet inserted is simply set; nothing else can hold its row.
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
