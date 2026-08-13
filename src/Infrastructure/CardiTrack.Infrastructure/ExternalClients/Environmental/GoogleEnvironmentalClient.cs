using System.Net.Http.Json;
using System.Text.Json.Serialization;
using CardiTrack.Application.Interfaces.Clients;
using CardiTrack.Infrastructure.Settings;
using Microsoft.Extensions.Logging;

namespace CardiTrack.Infrastructure.ExternalClients.Environmental;

/// <summary>
/// Ambient temperature and air quality via Google Maps Platform's Weather and Air Quality APIs —
/// one vendor for both, chosen for the shared billing/IAM surface (docs/llm_design.md). Both
/// calls read "current conditions" at the coordinate: exercise sessions are enriched shortly
/// after they complete, so current conditions approximate the session's conditions closely
/// enough for the qualitative trend narrative this feeds — a dedicated history endpoint is a
/// future refinement, not a correctness requirement of the enrichment feature itself.
/// <para>
/// Both calls take the coordinate as a request parameter and return only derived
/// measurements — the coordinate itself never appears in this class's return value, logs, or
/// any other output, matching the "raw coordinates are never persisted" rule the DPIA-facing
/// design commits to.
/// </para>
/// </summary>
public class GoogleEnvironmentalClient : IEnvironmentalContextClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly EnvironmentalContextSettings _settings;
    private readonly string _httpClientName;
    private readonly ILogger<GoogleEnvironmentalClient> _logger;

    public GoogleEnvironmentalClient(
        IHttpClientFactory httpClientFactory,
        EnvironmentalContextSettings settings,
        string httpClientName,
        ILogger<GoogleEnvironmentalClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _settings = settings;
        _httpClientName = httpClientName;
        _logger = logger;
    }

    public async Task<EnvironmentalContext> GetContextAsync(
        double latitude, double longitude, DateTime timestampUtc, CancellationToken ct = default)
    {
        var client = _httpClientFactory.CreateClient(_httpClientName);

        // Independent calls to independent APIs — a failure in one must not cost the other.
        var weatherTask = TryGetWeatherAsync(client, latitude, longitude, ct);
        var airQualityTask = TryGetAirQualityAsync(client, latitude, longitude, ct);
        await Task.WhenAll(weatherTask, airQualityTask);

        return new EnvironmentalContext(
            weatherTask.Result?.TemperatureCelsius,
            airQualityTask.Result?.Aqi,
            airQualityTask.Result?.Category,
            weatherTask.Result?.Condition,
            weatherTask.Result?.HumidityPercent);
    }

    /// <summary>
    /// The current-conditions call. Temperature, the described condition and humidity all come back
    /// from this one lookup — reading the fields already in the response rather than making a second
    /// call for them.
    /// </summary>
    private async Task<WeatherReading?> TryGetWeatherAsync(
        HttpClient client, double latitude, double longitude, CancellationToken ct)
    {
        try
        {
            var url = $"{_settings.WeatherBaseUrl}/v1/currentConditions:lookup" +
                $"?key={Uri.EscapeDataString(_settings.ApiKey)}" +
                $"&location.latitude={latitude.ToString(System.Globalization.CultureInfo.InvariantCulture)}" +
                $"&location.longitude={longitude.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

            using var response = await client.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Environmental weather lookup returned HTTP {StatusCode}.", (int)response.StatusCode);
                return null;
            }

            var body = await response.Content.ReadFromJsonAsync<WeatherCurrentConditionsResponse>(
                cancellationToken: ct);
            if (body is null)
                return null;

            double? celsius = null;
            if (body.Temperature?.Degrees is { } degrees)
            {
                // The API's default unit is Celsius, but responds in whatever the request implies —
                // this client sends none, so Fahrenheit is only ever seen if that default changes.
                // Converted rather than trusted, since the stored column's unit is fixed.
                celsius = string.Equals(body.Temperature.Unit, "FAHRENHEIT", StringComparison.OrdinalIgnoreCase)
                    ? (degrees - 32) * 5.0 / 9.0
                    : degrees;
            }

            // The human-readable description first, the enum-style type as a fallback: the former
            // is what belongs in a sentence a caregiver reads, the latter ("LIGHT_RAIN") is only
            // better than nothing. Each field is independently optional — a response missing one is
            // a partial reading, not a failed one.
            var condition = string.IsNullOrWhiteSpace(body.WeatherCondition?.Description?.Text)
                ? body.WeatherCondition?.Type
                : body.WeatherCondition.Description.Text;

            return new WeatherReading(celsius, condition, body.RelativeHumidity);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested
            && ex is HttpRequestException or TaskCanceledException
            or System.Text.Json.JsonException or NotSupportedException)
        {
            // Enrichment is best-effort context, not a required field: a transient failure here
            // must not cost the session's air-quality reading or fail the whole enrich pass.
            // JsonException/NotSupportedException cover a malformed or non-JSON body (e.g. an
            // HTML error page from a misrouted request) — ReadFromJsonAsync throws those, and an
            // uncaught one here would fault this task and take the sibling call down with it via
            // Task.WhenAll, defeating the "independent calls" guarantee this class documents.
            //
            // The token check separates the two things TaskCanceledException means here. An
            // HttpClient timeout raises it with the token unsignalled, and that is a lookup that
            // failed — swallowed, like any other. A signalled token is the host shutting the job
            // down, and swallowing that would turn a prompt shutdown into a full pass over every
            // remaining member, each one logging a warning on its way out.
            _logger.LogWarning(ex, "Environmental weather lookup failed.");
            return null;
        }
    }

    /// <summary>What one current-conditions lookup yielded; every field independently optional.</summary>
    private sealed record WeatherReading(double? TemperatureCelsius, string? Condition, int? HumidityPercent);

    private async Task<(int? Aqi, string? Category)?> TryGetAirQualityAsync(
        HttpClient client, double latitude, double longitude, CancellationToken ct)
    {
        try
        {
            var url = $"{_settings.BaseUrl}/v1/currentConditions:lookup" +
                $"?key={Uri.EscapeDataString(_settings.ApiKey)}";
            var body = new AirQualityLookupRequest
            {
                Location = new AirQualityLocation { Latitude = latitude, Longitude = longitude },
            };

            using var response = await client.PostAsJsonAsync(url, body, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Environmental air-quality lookup returned HTTP {StatusCode}.", (int)response.StatusCode);
                return null;
            }

            var parsed = await response.Content.ReadFromJsonAsync<AirQualityLookupResponse>(
                cancellationToken: ct);
            // The Universal AQI index is the one every response is guaranteed to carry; other
            // indexes vary by region.
            var universal = parsed?.Indexes?.FirstOrDefault(i => i.Code == "uaqi");
            return universal is null ? null : (universal.Aqi, universal.Category);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested
            && ex is HttpRequestException or TaskCanceledException
            or System.Text.Json.JsonException or NotSupportedException)
        {
            // See TryGetWeatherAsync's catch clause for why JsonException/NotSupportedException are
            // included alongside the transport exceptions, and why a signalled token is let through
            // rather than swallowed with the timeouts.
            _logger.LogWarning(ex, "Environmental air-quality lookup failed.");
            return null;
        }
    }

    private sealed record WeatherCurrentConditionsResponse
    {
        [JsonPropertyName("temperature")] public WeatherTemperature? Temperature { get; init; }
        [JsonPropertyName("weatherCondition")] public WeatherConditionPayload? WeatherCondition { get; init; }
        [JsonPropertyName("relativeHumidity")] public int? RelativeHumidity { get; init; }
    }

    private sealed record WeatherTemperature
    {
        [JsonPropertyName("degrees")] public double? Degrees { get; init; }
        [JsonPropertyName("unit")] public string? Unit { get; init; }
    }

    private sealed record WeatherConditionPayload
    {
        /// <summary>An enum-style code such as "LIGHT_RAIN" — the fallback when no description came back.</summary>
        [JsonPropertyName("type")] public string? Type { get; init; }

        [JsonPropertyName("description")] public WeatherConditionDescription? Description { get; init; }
    }

    /// <summary>
    /// The localised description. The API returns it in the locale the request implies; this client
    /// sends none, so it arrives in the service default — worth knowing, since the text goes into a
    /// prompt rather than being matched against.
    /// </summary>
    private sealed record WeatherConditionDescription
    {
        [JsonPropertyName("text")] public string? Text { get; init; }
    }

    private sealed record AirQualityLookupRequest
    {
        [JsonPropertyName("location")] public required AirQualityLocation Location { get; init; }
    }

    private sealed record AirQualityLocation
    {
        [JsonPropertyName("latitude")] public required double Latitude { get; init; }
        [JsonPropertyName("longitude")] public required double Longitude { get; init; }
    }

    private sealed record AirQualityLookupResponse
    {
        [JsonPropertyName("indexes")] public List<AirQualityIndex>? Indexes { get; init; }
    }

    private sealed record AirQualityIndex
    {
        [JsonPropertyName("code")] public string? Code { get; init; }
        [JsonPropertyName("aqi")] public int? Aqi { get; init; }
        [JsonPropertyName("category")] public string? Category { get; init; }
    }
}
