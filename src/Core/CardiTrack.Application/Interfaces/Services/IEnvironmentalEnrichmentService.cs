namespace CardiTrack.Application.Interfaces.Services;

/// <summary>
/// The environmental-context enrichment pass (docs/llm_design.md): for consent-granted members
/// with a GPS-tagged exercise session, look up ambient temperature and air quality for the
/// session and store the derived reading. Runs as its own Cloud Run job
/// (<c>CardiTrack.PipelineJobs --job enrich</c>) rather than inside the routine device sync —
/// exercise sessions are sparse and don't need 5-minute freshness, and an external vendor call
/// does not belong in the hot sync loop 10,000 devices go through every few minutes.
/// </summary>
public interface IEnvironmentalEnrichmentService
{
    /// <summary>Runs one pass; returns the number of readings written.</summary>
    Task<int> EnrichDueSessionsAsync(DateTime utcNow, CancellationToken ct = default);
}
