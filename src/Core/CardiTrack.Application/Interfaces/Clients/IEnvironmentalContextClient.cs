namespace CardiTrack.Application.Interfaces.Clients;

/// <summary>Ambient conditions and air quality for one location and time — derived values
/// only, the entire contract this client exposes past the coordinates it was called with.</summary>
/// <param name="WeatherCondition">
/// What the weather was doing in words ("Light rain", "Partly cloudy"). Free text from an external
/// service, so every consumer treats it as untrusted input rather than a known vocabulary.
/// </param>
/// <param name="RelativeHumidityPercent">
/// Humidity, which changes how heat is experienced — 28°C at 80% humidity is a different afternoon
/// from 28°C at 30%, and the same difference shows up in a heart rate.
/// </param>
public sealed record EnvironmentalContext(
    double? TemperatureCelsius,
    int? AirQualityIndex,
    string? AirQualityCategory,
    string? WeatherCondition = null,
    int? RelativeHumidityPercent = null);

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
