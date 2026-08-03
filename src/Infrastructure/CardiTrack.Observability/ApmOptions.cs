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

    // Terraform provisions Secret Manager-backed env vars as REPLACE_ME placeholders
    // until an operator sets real values (see infrastructure/deployments/secret_manager.tf);
    // a placeholder must behave like "not configured", not ship to a garbage endpoint.
    private const string TerraformPlaceholder = "REPLACE_ME";

    public bool IsConfigured =>
        HasRealValue(Engine)
        && HasRealValue(Data.IngestUrl)
        && HasRealValue(Data.IngestToken);

    private static bool HasRealValue(string? value) =>
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
    /// Provider-specific connection details beyond the two every backend shares — bound
    /// from Apm:Data:Extra (env form Apm__Data__Extra__[Key]). Lets a future engine take
    /// e.g. a region or dataset name without changing this schema or the apps.
    /// </summary>
    public Dictionary<string, string?> Extra { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
