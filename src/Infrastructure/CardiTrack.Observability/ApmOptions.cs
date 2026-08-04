using System.Runtime.Serialization;
using CardiTrack.Shared.Json;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog.Events;

namespace CardiTrack.Observability;

/// <summary>
/// APM backend selection and connection data, bound from the "Apm" config section.
/// Engine names a provider in <see cref="ApmProviderRegistry"/>; Data carries the
/// provider-agnostic connection info. Shipping is disabled until all three are set,
/// so committed appsettings can pre-select the engine while tokens stay in env vars
/// or secret stores (Apm__Data__IngestUrl / Apm__Data__IngestToken).
/// </summary>
public sealed class ApmOptions
{
    public const string SectionName = "Apm";

    /// <summary>Provider name, e.g. "BetterStack" (case-insensitive). Empty disables shipping.</summary>
    public string? Engine { get; set; }

    public ApmData Data { get; set; } = new();

    /// <summary>Minimum Serilog level shipped to the backend. Default Warning — free-tier prudence.</summary>
    public string MinimumLogLevel { get; set; } = "Warning";

    /// <summary>Head-sampling ratio for OTel traces, 0.0–1.0. Default 0.2 — free-tier prudence.</summary>
    public double TracesSampleRatio { get; set; } = 0.2;

    /// <summary>
    /// Opt-in switch for OTel metrics export (runtime, ASP.NET Core, HttpClient, Npgsql).
    /// Default off — meters stream around the clock and metrics bill as custom metrics on
    /// most backends. Deployed as the plaintext Apm__MetricsEnabled env var, owned by the
    /// apm_metrics_enabled tfvar.
    /// </summary>
    public bool MetricsEnabled { get; set; }

    // Terraform provisions Secret Manager-backed env vars as REPLACE_ME placeholders
    // until an operator sets real values (see infrastructure/deployments/secret_manager.tf);
    // a placeholder must behave like "not configured", not ship to a garbage endpoint.
    internal const string TerraformPlaceholder = "REPLACE_ME";

    public bool IsConfigured =>
        HasRealValue(Engine)
        && HasRealValue(Data.IngestUrl)
        && HasRealValue(Data.IngestToken);

    internal static bool HasRealValue(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && !string.Equals(value.Trim(), TerraformPlaceholder, StringComparison.Ordinal);

    public LogEventLevel ShipLevel =>
        Enum.TryParse<LogEventLevel>(MinimumLogLevel, ignoreCase: true, out var level)
            ? level
            : LogEventLevel.Warning;

    public double ClampedSampleRatio => Math.Clamp(TracesSampleRatio, 0.0, 1.0);
}

public sealed class ApmData
{
    /// <summary>Base ingestion URL/host for the backend; a bare host is treated as https.</summary>
    public string? IngestUrl { get; set; }

    /// <summary>Source/API token for the backend. Keep out of git.</summary>
    public string? IngestToken { get; set; }

    /// <summary>
    /// Provider-specific connection details beyond the two every backend shares. In the
    /// JSON form these are simply any other top-level keys (or an "Extra" object); in the
    /// section form they bind from Apm:Data:Extra. Lets a future engine take e.g. a
    /// region or dataset name without changing this schema or the apps.
    /// </summary>
    public Dictionary<string, string?> Extra { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Unknown top-level keys collected by Newtonsoft during deserialization; folded
    /// into <see cref="Extra"/> below so providers see one flat dictionary.
    /// </summary>
    [JsonExtensionData]
    private IDictionary<string, JToken>? _unknownKeys;

    [OnDeserialized]
    private void OnDeserialized(StreamingContext _)
    {
        if (_unknownKeys is null)
            return;
        foreach (var (key, value) in _unknownKeys)
            Extra[key] = AsString(value);
        _unknownKeys = null;
    }

    /// <summary>
    /// Parses the single-value form of Apm:Data — one JSON object carried by the
    /// Apm__Data env var (one secret), e.g. {"IngestUrl":"...","IngestToken":"..."}.
    /// The whole block goes through <see cref="JsonUtility"/> typed as ApmData; known
    /// keys match case-insensitively and every other key lands in <see cref="Extra"/>.
    /// </summary>
    public static ApmData FromJson(string json)
    {
        try
        {
            return JsonUtility.Deserialize<ApmData>(json);
        }
        catch (JsonDeserializationException ex)
        {
            // Deliberately NOT rethrowing with `ex` as inner: its message echoes the payload,
            // and this payload carries the ingest token. Length + error locations are enough
            // to remediate (fix the apm-data secret and re-roll).
            throw new InvalidOperationException(
                $"{ApmOptions.SectionName}:Data was provided as a single value but could not be read as " +
                "an object like {\"IngestUrl\":\"...\",\"IngestToken\":\"...\"} " +
                $"({json.Length} chars). Errors: {string.Join("; ", ex.Errors)}");
        }
    }

    private static string? AsString(JToken value) => value switch
    {
        JValue { Type: JTokenType.Null } => null,
        JValue { Type: JTokenType.String } text => (string?)text,
        _ => value.ToString(Formatting.None),
    };
}
