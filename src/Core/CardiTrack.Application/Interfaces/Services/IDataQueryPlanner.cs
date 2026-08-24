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
    /// <param name="allowedSources">
    /// The registry slice this workflow may plan over — the catalogue entry's
    /// <c>AllowedDatasets</c>. The prompt offers only these and the parse is intersected with
    /// them, so the planner is never offered what the validator would refuse. Null means every
    /// registered source, which is the pre-catalogue behaviour and what existing callers get.
    /// </param>
    Task<AiGenerationResult<DataQueryPlan>> PlanAsync(
        string question,
        string? conversationHistory = null,
        IReadOnlyList<DataQueryKind>? allowedSources = null,
        CancellationToken ct = default);
}
