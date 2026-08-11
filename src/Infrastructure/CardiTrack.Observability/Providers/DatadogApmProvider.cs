using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Sinks.OpenTelemetry;

namespace CardiTrack.Observability.Providers;

/// <summary>
/// Datadog (agentless): logs, traces and metrics all ship via OTLP/HTTP to the org's
/// per-signal intake with a dd-api-key header — the same pipeline end to end, so
/// Datadog correlates all three off one shared OTel Resource (service.name,
/// service.version, deployment.environment) instead of manual tag/attribute matching.
/// Logs used to ship through Datadog's classic HTTP log intake (a different product
/// surface with its own reserved-attribute correlation convention), which is why logs
/// and traces never actually joined up in the UI even once both were shipping.
/// Data translation: IngestUrl = Datadog site (e.g. datadoghq.eu, uk1.datadoghq.com),
/// IngestToken = API key, Extra["TraceEndpoint"] = the OTLP traces intake URL
/// (per-site pattern https://otlp.[site]/v1/traces, but org-entitlement-gated — a 403
/// means the org needs intake access via support, or the org's "OTLP Ingest" toggle
/// under Organization Settings -> API Keys). Without it this provider ships logs only.
/// The logs and metrics intake URLs are derived from the site
/// (https://otlp.[site]/v1/logs, /v1/metrics); Extra["LogsEndpoint"]/
/// Extra["MetricsEndpoint"] override them.
/// </summary>
public sealed class DatadogApmProvider : IApmProvider
{
    public const string EngineName = "Datadog";
    public const string TraceEndpointKey = "TraceEndpoint";
    public const string LogsEndpointKey = "LogsEndpoint";
    public const string MetricsEndpointKey = "MetricsEndpoint";

    public string Name => EngineName;

    /// <summary>
    /// The Resource attributes here must match <see cref="ApmExtensions"/>'s trace/metric
    /// resource exactly (same service.name/service.version/deployment.environment keys) —
    /// that shared identity, not the log record's attributes, is what lets Datadog treat
    /// all three signals as one service and resolve a log's "Related Trace".
    /// </summary>
    public LoggerConfiguration AddLogShipping(
        LoggerConfiguration loggerConfiguration, ApmOptions options, string serviceName) =>
        loggerConfiguration.WriteTo.Logger(
            lc => lc.WriteTo.OpenTelemetry(otlp =>
            {
                otlp.Endpoint = LogsIntakeUrl(options);
                otlp.Protocol = OtlpProtocol.HttpProtobuf;
                otlp.Headers = new Dictionary<string, string> { ["dd-api-key"] = options.Data.IngestToken! };
                otlp.ResourceAttributes = ResourceAttributes(options, serviceName);
            }),
            restrictedToMinimumLevel: options.ShipLevel);

    private static Dictionary<string, object> ResourceAttributes(ApmOptions options, string serviceName)
    {
        var attributes = new Dictionary<string, object>
        {
            ["service.name"] = serviceName,
            ["service.version"] = DeploymentInfo.Version,
        };

        if (DeploymentInfo.EnvironmentName is { } environmentName)
        {
            attributes["deployment.environment.name"] = environmentName;
            attributes["deployment.environment"] = environmentName;
        }

        return attributes;
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

    /// <summary>
    /// Log intake always ships (options are only checked as configured once IngestUrl is
    /// real), plus trace/metrics intake hosts when those signals are actually live — an
    /// unset TraceEndpoint or an IngestUrl the metrics URL can't be derived from means that
    /// host is never opened, so it has nothing to exclude from instrumentation. Logs, traces
    /// and metrics now all resolve to the same otlp.&lt;site&gt; host once every signal is
    /// live, so this collapses to a single entry in the common case — not a bug, just the
    /// one-pipeline design paying off.
    /// </summary>
    public IReadOnlyCollection<string> ShippingHosts(ApmOptions options)
    {
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (Uri.TryCreate(LogsIntakeUrl(options), UriKind.Absolute, out var logsUri))
            hosts.Add(logsUri.Host);

        var traceEndpoint = options.Data.Extra.GetValueOrDefault(TraceEndpointKey);
        if (!string.IsNullOrWhiteSpace(traceEndpoint) && Uri.TryCreate(traceEndpoint, UriKind.Absolute, out var traceUri))
            hosts.Add(traceUri.Host);

        if (options.MetricsEnabled
            && MetricsIntakeUrl(options) is { } metricsEndpoint
            && Uri.TryCreate(metricsEndpoint, UriKind.Absolute, out var metricsUri))
            hosts.Add(metricsUri.Host);

        return hosts;
    }

    /// <summary>
    /// Logs intake URL: an explicit Extra["LogsEndpoint"] wins; otherwise derived from a
    /// bare site name per the documented per-site pattern (https://otlp.[site]/v1/logs). A
    /// full URL passed as IngestUrl is used as-is. Unlike <see cref="MetricsIntakeUrl"/>,
    /// this never returns null: logs are the one signal this provider always ships once
    /// <see cref="ApmOptions.IsConfigured"/> is true, so there is no "not derivable, skip
    /// this signal" case to represent.
    /// </summary>
    public static string LogsIntakeUrl(ApmOptions options)
    {
        var explicitUrl = options.Data.Extra.GetValueOrDefault(LogsEndpointKey);
        if (!string.IsNullOrWhiteSpace(explicitUrl))
            return explicitUrl.Trim();

        var site = options.Data.IngestUrl!.Trim().TrimEnd('/');
        return site.Contains("://", StringComparison.Ordinal)
            ? site
            : $"https://otlp.{site}/v1/logs";
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
