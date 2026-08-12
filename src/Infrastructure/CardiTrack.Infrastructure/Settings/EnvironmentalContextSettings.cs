namespace CardiTrack.Infrastructure.Settings;

/// <summary>Google Maps Platform credentials for the environmental-enrichment client
/// (Weather + Air Quality APIs — one vendor, one key, per docs/llm_design.md).</summary>
public class EnvironmentalContextSettings
{
    public string ApiKey { get; set; } = string.Empty;

    public string BaseUrl { get; set; } = "https://airquality.googleapis.com";

    public string WeatherBaseUrl { get; set; } = "https://weather.googleapis.com";

    /// <summary>Low-volume, request-per-workout calls — a short timeout keeps a slow response
    /// from stalling the enrich job's per-session loop.</summary>
    public int TimeoutSeconds { get; set; } = 15;
}
