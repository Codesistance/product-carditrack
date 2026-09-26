using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;

namespace CardiTrack.Application.Interfaces.Services;

/// <summary>
/// A caregiver's persisted, multi-turn conversation about one CardiMember — malicious-check,
/// data-query planning, MedGemma clinical read, and rewrite to caregiver-plain-language, entirely
/// in-estate. See docs/compliance/dpia.md for the processing-operation entry this pipeline is
/// documented under.
/// </summary>
public interface IMemberChatService
{
    /// <summary>
    /// Sends one message, auto-creating or continuing the caregiver's active session for this
    /// member. Throws <see cref="KeyNotFoundException"/> if the caller may not view this member
    /// (404, not 403 — see <c>ICardiMemberAccessService</c>), and <see cref="ArgumentException"/> if
    /// the message flattens to nothing or the malicious/off-topic check rejects it.
    /// </summary>
    Task<MemberChatMessageResponse> SendMessageAsync(
        Guid userId, Guid cardiMemberId, string message, CancellationToken ct = default);

    /// <summary>
    /// <see cref="SendMessageAsync(Guid, Guid, string, CancellationToken)"/>, reporting to
    /// <paramref name="progress"/> as it runs — what the streaming endpoint relays to the app:
    /// each pipeline step as it starts, and the first reply as a draft before the answer check
    /// reads it. Steps are reported only after the malicious pre-check has passed, so every
    /// failure that has its own HTTP status (access, empty message, refusal) happens before the
    /// first report. Paths answered without a model — a journal yes or no, a message with no
    /// question, a settings confirmation — report nothing.
    /// <para>
    /// Each step arrives numbered (<see cref="MemberChatStep.Index"/>, and
    /// <see cref="MemberChatStep.Total"/> once the route is known). On a route that reads the
    /// readings — the long paths — question-specific waiting lines are generated alongside the
    /// pipeline and reported through <see cref="IMemberChatSendProgress.WaitingLines"/> if they
    /// are ready before the send settles.
    /// </para>
    /// </summary>
    /// <remarks>
    /// A reply the check finds did not address the question is retried once with the gap named,
    /// but only here, with a caller shown the first answer while the second is worked on: the
    /// plain send has no one to show a draft to, and would simply take twice as long.
    /// </remarks>
    Task<MemberChatMessageResponse> SendMessageAsync(
        Guid userId, Guid cardiMemberId, string message, IMemberChatSendProgress? progress,
        CancellationToken ct = default);

    /// <summary>The caregiver's active session for this member and its turns, or null if none
    /// exists — what a relaunched app resumes from.</summary>
    Task<MemberChatHistoryResponse?> GetCurrentSessionAsync(
        Guid userId, Guid cardiMemberId, CancellationToken ct = default);

    /// <summary>
    /// This caregiver's completed conversations about this member, newest started first — the
    /// chat sheet's history list. The active conversation is never in it, and sessions that never
    /// got a caregiver question are omitted: there is nothing to recognise them by. Throws
    /// <see cref="KeyNotFoundException"/> when the caller may not view this member.
    /// </summary>
    Task<MemberChatSessionListResponse> GetSessionsAsync(
        Guid userId, Guid cardiMemberId, CancellationToken ct = default);

    /// <summary>
    /// Ends the caregiver's active conversation about this member, so their next message starts
    /// fresh and the ended conversation moves to the history list at once. A no-op result (null
    /// id) when nothing is active. Throws <see cref="KeyNotFoundException"/> when the caller may
    /// not view this member.
    /// </summary>
    Task<MemberChatEndSessionResponse> EndCurrentSessionAsync(
        Guid userId, Guid cardiMemberId, CancellationToken ct = default);

    /// <summary>
    /// Reopens a completed conversation as the active one — its ended mark cleared, its
    /// last-activity brought to now — and returns its turns for the chat window to continue
    /// from. Any other conversation that was active is ended in the same stroke: a caregiver has
    /// one live conversation per member, and continuing an old one is choosing it. Throws
    /// <see cref="KeyNotFoundException"/> under the same existence-hiding rule as
    /// <see cref="GetSessionAsync"/>.
    /// </summary>
    Task<MemberChatHistoryResponse> ContinueSessionAsync(
        Guid userId, Guid cardiMemberId, Guid sessionId, CancellationToken ct = default);

    /// <summary>
    /// One conversation and its turns, read back for the history list. Throws
    /// <see cref="KeyNotFoundException"/> when the caller may not view this member, or when the
    /// session is not this caregiver's own conversation about this member — the same
    /// existence-hiding 404 the access check uses, so a guessed id learns nothing.
    /// </summary>
    Task<MemberChatHistoryResponse> GetSessionAsync(
        Guid userId, Guid cardiMemberId, Guid sessionId, CancellationToken ct = default);

    /// <summary>
    /// Permanently deletes conversations from this caregiver's history about this member — the
    /// sessions, their turns and their usage rows, gone for good; the client warns before asking.
    /// Ids that do not exist, or are not this caregiver's own conversations about this member,
    /// are skipped rather than failing the batch — the same existence-hiding stance as
    /// <see cref="GetSessionAsync"/>, expressed as idempotence: a guessed id deletes nothing and
    /// learns nothing. Throws <see cref="KeyNotFoundException"/> only when the caller may not
    /// view this member at all, and <see cref="ArgumentException"/> when the batch exceeds
    /// <see cref="MemberChatDeleteSessionsRequest.MaxBatchSize"/> ids — the same cap the HTTP
    /// boundary enforces by validation.
    /// </summary>
    Task<MemberChatDeleteSessionsResponse> DeleteSessionsAsync(
        Guid userId, Guid cardiMemberId, IReadOnlyList<Guid> sessionIds, CancellationToken ct = default);

    /// <summary>
    /// Short lines for the app to cycle in the reply slot while <see cref="SendMessageAsync"/>
    /// runs — generated from the question by the Rewrite slot so the wait narrates the actual
    /// checking. Never throws for a generation failure: canned lines come back instead, because
    /// waiting copy must not make the send it accompanies look broken. Still throws
    /// <see cref="KeyNotFoundException"/> when the caller may not view this member.
    /// </summary>
    Task<IReadOnlyList<string>> GetWaitingSentencesAsync(
        Guid userId, Guid cardiMemberId, string message, CancellationToken ct = default);

    /// <summary>
    /// Three question chips for the chat's empty state: this caregiver's own last three distinct
    /// questions about this member when they have any (topped up from the standard set when
    /// fewer), else the standard three derived deterministically from the member's data state —
    /// an unresolved alert earns an alert question. No model call. Throws
    /// <see cref="KeyNotFoundException"/> when the caller may not view this member.
    /// </summary>
    Task<MemberChatSuggestionsResponse> GetSuggestionsAsync(
        Guid userId, Guid cardiMemberId, CancellationToken ct = default);
}
