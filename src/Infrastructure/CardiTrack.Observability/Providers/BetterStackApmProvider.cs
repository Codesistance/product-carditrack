using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Serilog;

namespace CardiTrack.Observability.Providers;

/// <summary>
/// Better Stack: logs via the official Serilog sink, traces and metrics via OTLP/HTTP
/// to the per-source ingesting host (https://[host]/v1/[signal], bearer source token).
/// </summary>
public sealed class BetterStackApmProvider : IApmProvider
{
    public const string EngineName = "BetterStack";

    public string Name => EngineName;

    // serviceName is unused: a Better Stack source IS the service — the source token routes
    // logs to it, so there is no per-log service field to set. Traces still carry the name
    // through the OTel resource.
    public LoggerConfiguration AddLogShipping(
        LoggerConfiguration loggerConfiguration, ApmOptions options, string serviceName) =>
        loggerConfiguration.WriteTo.BetterStack(
            sourceToken: options.Data.IngestToken!,
            betterStackEndpoint: NormalizeIngestUrl(options.Data.IngestUrl!),
            restrictedToMinimumLevel: options.ShipLevel,
            // Cap the in-memory retry queue so a Better Stack outage can't balloon the process.
            queueLimitBytes: 10 * 1024 * 1024);

    public void AddTraceExporter(TracerProviderBuilder tracing, ApmOptions options) =>
        tracing.AddOtlpExporter(exporter =>
        {
            exporter.Endpoint = new Uri($"{NormalizeIngestUrl(options.Data.IngestUrl!)}/v1/traces");
            exporter.Protocol = OtlpExportProtocol.HttpProtobuf;
            exporter.Headers = $"Authorization=Bearer {options.Data.IngestToken}";
        });

    public void AddMetricExporter(MeterProviderBuilder metrics, ApmOptions options) =>
        metrics.AddOtlpExporter(exporter =>
        {
            exporter.Endpoint = new Uri($"{NormalizeIngestUrl(options.Data.IngestUrl!)}/v1/metrics");
            exporter.Protocol = OtlpExportProtocol.HttpProtobuf;
            exporter.Headers = $"Authorization=Bearer {options.Data.IngestToken}";
        });

    public ApmShippingStatus Describe(ApmOptions options) =>
        new(options.MetricsEnabled ? ["logs", "traces", "metrics"] : ["logs", "traces"], []);

    // Logs, traces and metrics all ship to the same ingesting host (only the path differs),
    // so one host covers every signal.
    public IReadOnlyCollection<string> ShippingHosts(ApmOptions options) =>
        [new Uri(NormalizeIngestUrl(options.Data.IngestUrl!)).Host];

    /// <summary>Better Stack shows the ingesting host without a scheme; accept both forms.</summary>
    public static string NormalizeIngestUrl(string ingestUrl)
    {
        var url = ingestUrl.Trim().TrimEnd('/');
        return url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? url : $"https://{url}";
    }
}
