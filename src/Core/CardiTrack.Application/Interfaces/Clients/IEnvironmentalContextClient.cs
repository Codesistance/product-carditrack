namespace CardiTrack.Application.Interfaces.Clients;

/// <summary>Ambient temperature and air quality for one location and time — derived values
/// only, the entire contract this client exposes past the coordinates it was called with.</summary>
public sealed record EnvironmentalContext(
    double? TemperatureCelsius, int? AirQualityIndex, string? AirQualityCategory);

/// <summary>
/// Looks up ambient temperature and air quality for a coordinate and time. The port for the
/// exercise-session enrichment pass (docs/llm_design.md) — implemented against Google Maps
/// Platform's Weather and Air Quality APIs. Callers pass a coordinate in, once, for exactly one
/// lookup; nothing in this contract persists it, and neither may an implementation.
/// </summary>
public interface IEnvironmentalContextClient
{
    Task<EnvironmentalContext> GetContextAsync(
        double latitude, double longitude, DateTime timestampUtc, CancellationToken ct = default);
}
