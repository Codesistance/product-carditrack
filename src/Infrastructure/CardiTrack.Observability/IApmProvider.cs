using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Serilog;

namespace CardiTrack.Observability;

/// <summary>
/// One APM backend (Better Stack today, anything else tomorrow). Implementations are
/// stateless: they translate the generic <see cref="ApmOptions"/> into their backend's
/// Serilog sink and OTel exporters. Register new engines in <see cref="ApmProviderRegistry"/>.
/// </summary>
public interface IApmProvider
{
    /// <summary>Engine name matched (case-insensitively) against Apm:Engine.</summary>
    string Name { get; }

    /// <summary>
    /// Adds the backend's log sink. Only called when options are fully configured.
    /// <paramref name="serviceName"/> is the calling host's <see cref="ApmServiceNames"/>
    /// value — the same string its traces carry as service.name, so logs and spans land
    /// on one service.
    /// </summary>
    LoggerConfiguration AddLogShipping(LoggerConfiguration loggerConfiguration, ApmOptions options, string serviceName);

    /// <summary>Adds the backend's trace exporter. Only called when options are fully configured.</summary>
    void AddTraceExporter(TracerProviderBuilder tracing, ApmOptions options);

    /// <summary>
    /// Adds the backend's metric exporter. Only called when options are fully configured
    /// AND <see cref="ApmOptions.MetricsEnabled"/> is on.
    /// </summary>
    void AddMetricExporter(MeterProviderBuilder metrics, ApmOptions options);

    /// <summary>
    /// What this configuration will actually ship, for startup logging: signals that are
    /// live, plus a warning per signal that is requested-but-inoperative and how to fix it.
    /// Only called when options are fully configured.
    /// </summary>
    ApmShippingStatus Describe(ApmOptions options);
}

/// <summary>Startup-log view of a provider's effective shipping state.</summary>
/// <param name="Signals">Signals that will ship, e.g. ["logs", "traces"].</param>
/// <param name="Warnings">One entry per signal that is configured off/broken, with the remedy.</param>
public sealed record ApmShippingStatus(IReadOnlyList<string> Signals, IReadOnlyList<string> Warnings)
{
    public string Summary => string.Join("+", Signals);
}
