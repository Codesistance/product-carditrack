namespace CardiTrack.Application.Interfaces.Services;

/// <summary>
/// One pass of the real-time assessment job (docs/llm_design.md): for every member with fresh
/// granular data, run SSA over the latest hour of heart rate, ask the private medical model for
/// an assessment, store it, and raise an alert when the routed severity warrants one.
/// </summary>
public interface IRealtimeAssessmentService
{
    /// <summary>Assesses every due member once; returns how many assessments were written.</summary>
    Task<int> AssessDueMembersAsync(DateTime utcNow, CancellationToken ct = default);
}
