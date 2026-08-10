using System.Diagnostics;
using CardiTrack.Observability;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace CardiTrack.UnitTests.Observability;

public class ActivityLogEnricherTests
{
    private sealed class CapturingSink : ILogEventSink
    {
        public LogEvent? LastEvent { get; private set; }

        public void Emit(LogEvent logEvent) => LastEvent = logEvent;
    }

    private static LogEvent EmitLogEvent()
    {
        var sink = new CapturingSink();
        var logger = new LoggerConfiguration()
            .Enrich.With(new ActivityLogEnricher())
            .WriteTo.Sink(sink)
            .CreateLogger();

        logger.Information("test event");
        return sink.LastEvent!;
    }

    [Fact]
    public void Enrich_WithAmbientActivity_AddsTraceAndSpanIdAsHex()
    {
        using var activity = new Activity("test-activity").Start();

        var logEvent = EmitLogEvent();

        var traceId = Assert.IsType<ScalarValue>(logEvent.Properties["trace_id"]).Value as string;
        var spanId = Assert.IsType<ScalarValue>(logEvent.Properties["span_id"]).Value as string;
        Assert.Equal(activity.TraceId.ToHexString(), traceId);
        Assert.Equal(activity.SpanId.ToHexString(), spanId);
        Assert.Equal(32, traceId!.Length);
        Assert.Equal(16, spanId!.Length);
    }

    [Fact]
    public void Enrich_WithNoAmbientActivity_AddsNoTraceProperties()
    {
        Assert.Null(Activity.Current);

        var logEvent = EmitLogEvent();

        Assert.False(logEvent.Properties.ContainsKey("trace_id"));
        Assert.False(logEvent.Properties.ContainsKey("span_id"));
    }
}
