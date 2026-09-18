using System.Diagnostics.Tracing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CardiTrack.Observability;

/// <summary>
/// Surfaces OTLP export failures that would otherwise be silent. The OpenTelemetry .NET SDK
/// reports failed exports (network errors, non-2xx responses, serialization failures) only
/// through its internal <see cref="EventSource"/>s — nothing subscribes to those by default, so
/// a broken export just... doesn't ship, with no log line and no exception anywhere. Ported from
/// ConcairgeApp's <c>OtlpExportDiagnostics</c>, made provider-agnostic here (Concairge only wired
/// it for Datadog and left Dynatrace uncovered).
/// </summary>
internal sealed class OtlpExportDiagnostics : IHostedService, IDisposable
{
    private readonly ILogger<OtlpExportDiagnostics> _logger;
    private OtlpEventListener? _listener;

    public OtlpExportDiagnostics(ILogger<OtlpExportDiagnostics> logger) => _logger = logger;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _listener = new OtlpEventListener(_logger);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose() => _listener?.Dispose();

    private sealed class OtlpEventListener : EventListener
    {
        private readonly ILogger _logger;

        public OtlpEventListener(ILogger logger)
        {
            _logger = logger;
        }

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name.StartsWith("OpenTelemetry", StringComparison.Ordinal))
                EnableEvents(eventSource, EventLevel.Warning);
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            // Periodic metric polls, not diagnostics — would otherwise flood this at Warning.
            if (eventData.EventName == "EventCounters")
                return;

            try
            {
                var message = !string.IsNullOrEmpty(eventData.Message) && eventData.Payload?.Count > 0
                    ? string.Format(eventData.Message, [.. eventData.Payload])
                    : eventData.Message;

                if (string.IsNullOrEmpty(message))
                    return;

                Serilog.Debugging.SelfLog.WriteLine(
                    "[OTel:{0}/{1}] {2}", eventData.EventSource.Name, eventData.EventName, message);
                _logger.Log(
                    LevelFor(eventData.EventName),
                    "[OTel:{EventSource}/{EventName}] {Message}",
                    eventData.EventSource.Name, eventData.EventName, message);
            }
            catch
            {
                // Never let the diagnostics listener crash the app it's meant to be observing.
            }
        }
    }

    /// <summary>
    /// What level an SDK event is worth. Warning for everything by default — a failed export is
    /// otherwise silent, which is the whole reason this listener exists.
    /// </summary>
    /// <remarks>
    /// <c>MetricInstrumentIgnored</c> is the exception. The SDK raises it once per instrument
    /// belonging to a meter this app did not subscribe, on every start, so it reports a collection
    /// choice rather than anything going wrong. Left at Warning it was the single largest source of
    /// warnings the API wrote — four per start, ahead of every real one — and a warn stream that is
    /// mostly startup chatter is a warn stream nobody reads the export failures out of. Debug keeps
    /// the line for whoever is asking which meters are collected, at a level nothing pages on.
    /// </remarks>
    internal static LogLevel LevelFor(string? eventName) =>
        eventName == MetricInstrumentIgnoredEvent ? LogLevel.Debug : LogLevel.Warning;

    /// <summary>
    /// OpenTelemetry-Sdk's name for "this instrument belongs to a meter no provider subscribed".
    /// </summary>
    internal const string MetricInstrumentIgnoredEvent = "MetricInstrumentIgnored";
}
