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

    /// <summary>The caregiver's active session for this member and its turns, or null if none
    /// exists — what a relaunched app resumes from.</summary>
    Task<MemberChatHistoryResponse?> GetCurrentSessionAsync(
        Guid userId, Guid cardiMemberId, CancellationToken ct = default);

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
    /// Question chips for the chat's empty state, derived deterministically from the member's
    /// data state — an unresolved alert earns an alert question, and the rest name what the
    /// assistant can do. No model call. Throws <see cref="KeyNotFoundException"/> when the caller
    /// may not view this member.
    /// </summary>
    Task<MemberChatSuggestionsResponse> GetSuggestionsAsync(
        Guid userId, Guid cardiMemberId, CancellationToken ct = default);
}
