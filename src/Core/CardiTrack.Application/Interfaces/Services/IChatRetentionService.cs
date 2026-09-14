namespace CardiTrack.Application.Interfaces.Services;

/// <summary>What a chat retention sweep removed.</summary>
/// <param name="Sessions">Conversations deleted.</param>
/// <param name="Turns">Messages deleted with them.</param>
/// <param name="Usages">Per-call usage rows deleted with those messages.</param>
public sealed record ChatRetentionReport(int Sessions, int Turns, int Usages);

/// <summary>
/// Deletes member chat conversations once they pass their retention period — 90 days from a
/// conversation's last turn (issue #488, decided 2026-09-14 and published in the privacy policy).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Whole conversations, never individual turns.</strong> A thread trimmed message by
/// message leaves an answer with no question: less use to the caregiver, and no less health data
/// for having lost its context. The age of a conversation is the age of its newest turn, so a
/// thread somebody returns to every month is never half-deleted underneath them.
/// </para>
/// <para>
/// Finding and deleting are separate calls so the caller can rehearse: the worker lists what
/// would go, logs it, and deletes nothing when its DryRun switch is on
/// (docs/technical/data_protection_architecture.md §5.2).
/// </para>
/// </remarks>
public interface IChatRetentionService
{
    /// <summary>
    /// Conversations whose newest turn predates <paramref name="cutoffUtc"/>, oldest first, up to
    /// <paramref name="limit"/>. A conversation with no turns at all is dated from when it was
    /// started — otherwise one opened and never used would have no age and would never expire.
    /// </summary>
    Task<IReadOnlyList<Guid>> FindExpiredSessionsAsync(
        DateTime cutoffUtc, int limit, CancellationToken ct = default);

    /// <summary>
    /// Deletes the named conversations with their turns and usage rows, in one transaction.
    /// </summary>
    Task<ChatRetentionReport> DeleteSessionsAsync(
        IReadOnlyList<Guid> sessionIds, CancellationToken ct = default);
}
