using OpenTelemetry.Exporter;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Sinks.Datadog.Logs;

namespace CardiTrack.Observability.Providers;

/// <summary>
/// Datadog (agentless): logs via the official Serilog sink to the site's HTTP intake,
/// traces via OTLP/HTTP to the org's intake endpoint with a dd-api-key header.
/// Data translation: IngestUrl = Datadog site (e.g. datadoghq.eu, us5.datadoghq.com),
/// IngestToken = API key, Extra["TraceEndpoint"] = the org-specific OTLP traces intake
/// URL — Datadog does not publish a derivable per-site URL and gates the endpoint on
/// org entitlement, so without it this provider ships logs only.
/// </summary>
public sealed class DatadogApmProvider : IApmProvider
{
    public const string EngineName = "Datadog";
    public const string TraceEndpointKey = "TraceEndpoint";

    public string Name => EngineName;

    public LoggerConfiguration AddLogShipping(LoggerConfiguration loggerConfiguration, ApmOptions options) =>
        loggerConfiguration.WriteTo.DatadogLogs(
            apiKey: options.Data.IngestToken!,
            source: "csharp",
            service: "carditrack",
            configuration: new DatadogConfiguration(url: LogIntakeUrl(options.Data.IngestUrl!)),
            restrictedToMinimumLevel: options.ShipLevel);

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
}
