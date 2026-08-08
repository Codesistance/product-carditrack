using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Sinks.Datadog.Logs;

namespace CardiTrack.Observability.Providers;

/// <summary>
/// Datadog (agentless): logs via the official Serilog sink to the site's HTTP intake,
/// traces and metrics via OTLP/HTTP to the org's intake with a dd-api-key header.
/// Data translation: IngestUrl = Datadog site (e.g. datadoghq.eu, uk1.datadoghq.com),
/// IngestToken = API key, Extra["TraceEndpoint"] = the OTLP traces intake URL
/// (per-site pattern https://otlp.[site]/v1/traces, but org-entitlement-gated — a 403
/// means the org needs intake access via support). Without it this provider ships logs
/// only. The metrics intake URL is derived from the site (https://otlp.[site]/v1/metrics);
/// Extra["MetricsEndpoint"] overrides it.
/// </summary>
public sealed class DatadogApmProvider : IApmProvider
{
    public const string EngineName = "Datadog";
    public const string TraceEndpointKey = "TraceEndpoint";
    public const string MetricsEndpointKey = "MetricsEndpoint";

    public string Name => EngineName;

    public LoggerConfiguration AddLogShipping(
        LoggerConfiguration loggerConfiguration, ApmOptions options, string serviceName) =>
        loggerConfiguration.WriteTo.DatadogLogs(
            apiKey: options.Data.IngestToken!,
            source: "csharp",
            service: serviceName,
            tags: LogTags(),
            configuration: new DatadogConfiguration(url: LogIntakeUrl(options.Data.IngestUrl!)),
            restrictedToMinimumLevel: options.ShipLevel);

    /// <summary>
    /// The ddtags every log carries. Datadog's reserved tags — the ones behind the Service
    /// and Version facets, the environment selector, and release comparison — are read from
    /// the tag list and the sink's own service field, never from log attributes: the
    /// "Version" property the hosts enrich with shows up as an ordinary attribute and
    /// leaves the Version facet empty, which is why the release is repeated here as a tag.
    /// With the sink's service field, these complete the env/service/version triple.
    ///
    /// "env" is dropped when the environment is unknown rather than sent as a placeholder:
    /// an untagged log is findable and obviously unlabelled, whereas an "unknown"
    /// environment becomes a real value in the selector that nothing can be done about.
    /// </summary>
    public static string[] LogTags()
    {
        var tags = new List<string> { $"version:{DeploymentInfo.Version}" };

        if (DeploymentInfo.EnvironmentName is { } environmentName)
            tags.Add($"env:{environmentName}");

        return [.. tags];
    }

    public void AddTraceExporter(TracerProviderBuilder tracing, ApmOptions options)
    {
        var traceEndpoint = options.Data.Extra.GetValueOrDefault(TraceEndpointKey);
        if (string.IsNullOrWhiteSpace(traceEndpoint))
            return;

        tracing.AddOtlpExporter(exporter =>
        {
            exporter.Endpoint = new Uri(traceEndpoint);
            // Datadog's OTLP intake supports http/protobuf and http/json only — no gRPC.
            exporter.Protocol = OtlpExportProtocol.HttpProtobuf;
            exporter.Headers = $"dd-api-key={options.Data.IngestToken}";
        });
    }

    public void AddMetricExporter(MeterProviderBuilder metrics, ApmOptions options)
    {
        var metricsEndpoint = MetricsIntakeUrl(options);
        if (metricsEndpoint is null)
            return;

        metrics.AddOtlpExporter((exporter, reader) =>
        {
            exporter.Endpoint = new Uri(metricsEndpoint);
            exporter.Protocol = OtlpExportProtocol.HttpProtobuf;
            exporter.Headers = $"dd-api-key={options.Data.IngestToken}";
            // Datadog's OTLP metrics intake requires delta temporality; cumulative
            // sums are rejected or mis-graphed.
            reader.TemporalityPreference = MetricReaderTemporalityPreference.Delta;
        });
    }

    public ApmShippingStatus Describe(ApmOptions options)
    {
        var signals = new List<string> { "logs" };
        var warnings = new List<string>();

        if (string.IsNullOrWhiteSpace(options.Data.Extra.GetValueOrDefault(TraceEndpointKey)))
            warnings.Add(
                $"traces will not ship: {TraceEndpointKey} is not set in Apm:Data — add the org's OTLP "
                + "traces intake URL (https://otlp.<site>/v1/traces; 403 responses mean the org needs "
                + "intake access via Datadog support)");
        else
            signals.Add("traces");

        if (options.MetricsEnabled)
        {
            if (MetricsIntakeUrl(options) is null)
                warnings.Add(
                    $"metrics are enabled but the intake URL cannot be derived from IngestUrl "
                    + $"'{options.Data.IngestUrl}' — use a bare site name or set {MetricsEndpointKey} in Apm:Data");
            else
                signals.Add("metrics");
        }

        return new ApmShippingStatus(signals, warnings);
    }

    /// <summary>Derives the log intake URL from a bare site name; full URLs pass through.</summary>
    public static string LogIntakeUrl(string site)
    {
        var trimmed = site.Trim().TrimEnd('/');
        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return trimmed;
        return trimmed.StartsWith("http-intake.", StringComparison.OrdinalIgnoreCase)
            ? $"https://{trimmed}"
            : $"https://http-intake.logs.{trimmed}";
    }

    /// <summary>
    /// Metrics intake URL: an explicit Extra["MetricsEndpoint"] wins; otherwise derived
    /// from a bare site name per the documented per-site pattern. Null (nothing ships)
    /// when IngestUrl is a full URL/intake host the site can't be recovered from.
    /// </summary>
    public static string? MetricsIntakeUrl(ApmOptions options)
    {
        var explicitUrl = options.Data.Extra.GetValueOrDefault(MetricsEndpointKey);
        if (!string.IsNullOrWhiteSpace(explicitUrl))
            return explicitUrl.Trim();

        var site = options.Data.IngestUrl?.Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(site)
            || site.Contains("://", StringComparison.Ordinal)
            || site.StartsWith("http-intake.", StringComparison.OrdinalIgnoreCase))
            return null;
        return $"https://otlp.{site}/v1/metrics";
    }
}
