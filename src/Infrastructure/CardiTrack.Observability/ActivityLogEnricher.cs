using System.Diagnostics;
using Serilog.Core;
using Serilog.Events;

namespace CardiTrack.Observability;

/// <summary>
/// Writes the ambient <see cref="Activity"/>'s trace/span IDs onto every log event, so a
/// backend can jump from a log line to its trace. Serilog carries no enricher for this by
/// default, and neither <c>Serilog.Sinks.Datadog.Logs</c> nor <c>BetterStack.Logs.Serilog</c>
/// (the two sinks this project ships) add one on their own — logs and traces were, until this,
/// entirely uncorrelated.
///
/// The property names are the plain OpenTelemetry-standard ones ("trace_id"/"span_id"), not the
/// Datadog-tracer-specific "dd.trace_id"/"dd.span_id" decimal convention: Datadog's OTLP trace
/// intake (which is how this project ships traces) natively recognizes the OTel names in exactly
/// the hex format <see cref="ActivityTraceId.ToHexString"/>/<see cref="ActivitySpanId.ToHexString"/>
/// already produce, so no ID-format conversion is needed, and the same enricher works unchanged
/// for BetterStack, the other supported engine.
///
/// A no-op when there is no ambient Activity (local dev with no request in flight, or between
/// requests) — logs ship exactly as they did before this enricher existed.
/// </summary>
public sealed class ActivityLogEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        var activity = Activity.Current;
        if (activity is null)
            return;

        logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("trace_id", activity.TraceId.ToHexString()));
        logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("span_id", activity.SpanId.ToHexString()));
    }
}
