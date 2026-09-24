using OpenTelemetry.Exporter;
using OpenTelemetry;
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
    /// <remarks>
    /// Two sinks, not one: events carrying <see cref="LogRelay.ServiceProperty"/> were written
    /// by this host on behalf of the mobile app (see <c>MobileDiagnosticsController</c>) and
    /// ship under <see cref="ApmServiceNames.Mobile"/> instead, without this host's
    /// service.version — the app's own build is on the event as <c>ClientVersion</c>, and
    /// stamping the API's release on a phone's crash would pin it to the wrong deploy. Every
    /// other event is excluded from that sink and included in the host's, so nothing ships twice
    /// and nothing ships under two names.
    /// </remarks>
    public LoggerConfiguration AddLogShipping(
        LoggerConfiguration loggerConfiguration, ApmOptions options, string serviceName) =>
        loggerConfiguration
            .WriteTo.Logger(
                lc => lc
                    .Filter.ByExcluding(LogRelay.IsRelayed)
                    .Enrich.With(new DatadogLogStatusEnricher())
                    .WriteTo.OpenTelemetry(otlp => ConfigureLogsExporter(
                        otlp, options, ResourceAttributes(options, serviceName, includeVersion: true))),
                restrictedToMinimumLevel: options.ShipLevel)
            .WriteTo.Logger(
                lc => lc
                    .Filter.ByIncludingOnly(e => LogRelay.IsRelayedTo(e, ApmServiceNames.Mobile))
                    .Enrich.With(new DatadogLogStatusEnricher())
                    .WriteTo.OpenTelemetry(otlp => ConfigureLogsExporter(
                        otlp, options, ResourceAttributes(options, ApmServiceNames.Mobile, includeVersion: false))),
                restrictedToMinimumLevel: options.ShipLevel);

    private static void ConfigureLogsExporter(
        BatchedOpenTelemetrySinkOptions otlp, ApmOptions options, Dictionary<string, object> resourceAttributes)
    {
        // The enricher rides the sub-loggers, not the root: "level" is a Datadog intake
        // contract (see DatadogLogStatusEnricher), so the console and other engines
        // never carry it.
        otlp.Endpoint = LogsIntakeUrl(options);
        otlp.Protocol = OtlpProtocol.HttpProtobuf;
        otlp.Headers = new Dictionary<string, string> { ["dd-api-key"] = options.Data.IngestToken! };
        otlp.ResourceAttributes = resourceAttributes;

        // The same pooled-connection hygiene the trace and metric exporters get from
        // OtlpExportResilience. The sink builds its own HttpClient, so without this it ran on
        // default handler settings — the one export path in the process still exposed to the
        // stale-keep-alive race that class exists to prevent, and the one whose loss is total
        // for a job: a pass that lost its batches shipped nothing, and the root span it did ship
        // then pointed at logs that were never there.
        otlp.HttpMessageHandler = OtlpExportResilience.CreateTransportHandler();

        // The sink's own POSTs to the intake are HTTP requests like any other, and the HttpClient
        // instrumentation was recording each one as a client span under whatever activity was
        // current — an export of the pass's logs showing up inside the pass's trace as work the
        // pass did. The sink offers this hook for exactly that.
        otlp.OnBeginSuppressInstrumentation = SuppressInstrumentationScope.Begin;
    }

    private static Dictionary<string, object> ResourceAttributes(
        ApmOptions options, string serviceName, bool includeVersion)
    {
        var attributes = new Dictionary<string, object>
        {
            ["service.name"] = serviceName,
        };

        if (includeVersion)
            attributes["service.version"] = DeploymentInfo.Version;

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
    /// bare site name per the documented per-site pattern (https://otlp.[site]/v1/logs).
    /// Unlike <see cref="MetricsIntakeUrl"/>, this never returns null: logs are the one
    /// signal this provider always ships once <see cref="ApmOptions.IsConfigured"/> is
    /// true, so there is no "not derivable, skip this signal" case to represent.
    ///
    /// A full URL in IngestUrl is deliberately NOT passed through as-is: the most likely
    /// full URL to find there is the classic log-intake host from before logs moved onto
    /// OTLP (e.g. https://http-intake.logs.[site]), which would silently receive OTLP
    /// payloads it can't parse — Serilog.Sinks.OpenTelemetry drops failed batches without
    /// surfacing an error, so this would fail invisibly rather than loudly. Fail fast at
    /// startup instead, naming the explicit override.
    /// </summary>
    public static string LogsIntakeUrl(ApmOptions options)
    {
        var explicitUrl = options.Data.Extra.GetValueOrDefault(LogsEndpointKey);
        if (!string.IsNullOrWhiteSpace(explicitUrl))
            return explicitUrl.Trim();

        var site = options.Data.IngestUrl!.Trim().TrimEnd('/');
        if (site.Contains("://", StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Apm:Data:IngestUrl is a full URL ('{site}'), not a bare Datadog site — the logs OTLP "
                + "endpoint can't be safely inferred from it (it may still be the classic log intake host "
                + $"from before logs moved to OTLP). Set Apm:Data:Extra:{LogsEndpointKey} explicitly to the "
                + "correct https://otlp.<site>/v1/logs URL, or change IngestUrl back to a bare site name.");

        return $"https://otlp.{site}/v1/logs";
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
