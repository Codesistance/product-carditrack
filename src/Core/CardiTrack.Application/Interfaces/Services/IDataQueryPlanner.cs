using CardiTrack.Application.DTOs.Common;

namespace CardiTrack.Application.Interfaces.Services;

/// <summary>
/// Decides which existing, whitelisted data sources a caregiver's chat question needs — never which
/// member's. See <see cref="DataQueryPlan"/>'s remarks for why that split is enforced by the type,
/// not by convention.
/// </summary>
public interface IDataQueryPlanner
{
    Task<AiGenerationResult<DataQueryPlan>> PlanAsync(string question, CancellationToken ct = default);
}
