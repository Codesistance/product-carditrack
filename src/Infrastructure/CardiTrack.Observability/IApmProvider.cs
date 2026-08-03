using OpenTelemetry.Trace;
using Serilog;

namespace CardiTrack.Observability;

/// <summary>
/// One APM backend (Better Stack today, anything else tomorrow). Implementations are
/// stateless: they translate the generic <see cref="ApmOptions"/> into their backend's
/// Serilog sink and OTel exporter. Register new engines in <see cref="ApmProviderRegistry"/>.
/// </summary>
public interface IApmProvider
{
    /// <summary>Engine name matched (case-insensitively) against Apm:Engine.</summary>
    string Name { get; }

    /// <summary>Adds the backend's log sink. Only called when options are fully configured.</summary>
    LoggerConfiguration AddLogShipping(LoggerConfiguration loggerConfiguration, ApmOptions options);

    /// <summary>Adds the backend's trace exporter. Only called when options are fully configured.</summary>
    void AddTraceExporter(TracerProviderBuilder tracing, ApmOptions options);
}
