using CardiTrack.Application.DTOs.Common;

namespace CardiTrack.Application.Interfaces.Services;

/// <summary>
/// The one classifier of the member-chat redesign: which catalogue entry serves this question.
/// One job — classify. It does not choose data, windows or metrics; each workflow's own planner
/// does that downstream, already knowing which workflow it serves.
/// </summary>
public interface IChatRouter
{
    /// <param name="questionsOnlyHistory">
    /// The caregiver's prior questions in this session, framed — the same cut the clinical read
    /// gets, and for the routing call it is the only context that can change the answer: a
    /// follow-up like "why?" routes by what it follows. No registry, no member context — grounding
    /// the routing call is prompt weight that cannot change a classification.
    /// </param>
    Task<AiGenerationResult<ChatRouteDecision>> RouteAsync(
        string question, string? questionsOnlyHistory = null, CancellationToken ct = default);
}
