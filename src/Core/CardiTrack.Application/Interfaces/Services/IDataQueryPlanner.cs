using CardiTrack.Application.DTOs.Common;

namespace CardiTrack.Application.Interfaces.Services;

/// <summary>
/// Decides which existing, whitelisted data sources a caregiver's chat question needs — never which
/// member's. See <see cref="DataQueryPlan"/>'s remarks for why that split is enforced by the type,
/// not by convention.
/// </summary>
public interface IDataQueryPlanner
{
    /// <param name="conversationHistory">
    /// The framed earlier-turns block for this session, or null for a first question. A follow-up
    /// like "and how was her sleep that week?" names its window only in the earlier turns, so a
    /// planner shown the bare question alone picks the defaults instead of what the caregiver
    /// meant.
    /// </param>
    Task<AiGenerationResult<DataQueryPlan>> PlanAsync(
        string question, string? conversationHistory = null, CancellationToken ct = default);
}
