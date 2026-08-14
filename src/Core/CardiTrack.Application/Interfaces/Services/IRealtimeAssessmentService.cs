namespace CardiTrack.Application.Interfaces.Services;

/// <summary>
/// One pass of the real-time assessment job (docs/llm_design.md): for every member with fresh
/// granular data, run SSA over the latest hour of heart rate; only a jump from trend reaches
/// the private medical model. Store that assessment, and raise an alert when the routed
/// severity warrants one.
/// </summary>
public interface IRealtimeAssessmentService
{
    /// <summary>Assesses every due member once; returns how many assessments were written.</summary>
    Task<int> AssessDueMembersAsync(DateTime utcNow, CancellationToken ct = default);
}
