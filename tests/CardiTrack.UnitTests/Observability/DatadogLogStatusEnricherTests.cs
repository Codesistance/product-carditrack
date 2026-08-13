using CardiTrack.Observability.Providers;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace CardiTrack.UnitTests.Observability;

/// <summary>
/// The enricher's output is an intake contract: Datadog reads the "level" attribute verbatim and
/// never case-folds, so the value must arrive already canonical. The cases that matter are the
/// exact strings — a near-miss like "Error" or "warning" ships successfully and fails silently,
/// which is the very bug this exists to fix.
/// </summary>
public class DatadogLogStatusEnricherTests
{
    private sealed class CapturingSink : ILogEventSink
    {
        public LogEvent? LastEvent { get; private set; }

        public void Emit(LogEvent logEvent) => LastEvent = logEvent;
    }

    private static LogEvent EmitAt(LogEventLevel level, string template = "test event")
    {
        var sink = new CapturingSink();
        var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .Enrich.With(new DatadogLogStatusEnricher())
            .WriteTo.Sink(sink)
            .CreateLogger();

        logger.Write(level, template);
        return sink.LastEvent!;
    }

    private static string? LevelProperty(LogEvent logEvent) =>
        Assert.IsType<ScalarValue>(logEvent.Properties[DatadogLogStatusEnricher.AttributeName]).Value as string;

    /// <summary>
    /// Every Serilog level maps to the exact status Datadog's own C# integration pipeline
    /// produced on the classic intake — measured from this org's records, Fatal → emergency
    /// included, so the OTLP migration changes nothing about how statuses read.
    /// </summary>
    [Theory]
    [InlineData(LogEventLevel.Verbose, "debug")]
    [InlineData(LogEventLevel.Debug, "debug")]
    [InlineData(LogEventLevel.Information, "info")]
    [InlineData(LogEventLevel.Warning, "warn")]
    [InlineData(LogEventLevel.Error, "error")]
    [InlineData(LogEventLevel.Fatal, "emergency")]
    public void MapsEverySerilogLevelToTheCanonicalDatadogStatus(LogEventLevel level, string expected)
    {
        Assert.Equal(expected, LevelProperty(EmitAt(level)));
    }

    /// <summary>
    /// An event whose message template already binds a "level" property keeps its own value —
    /// the enricher must not clobber user data to win an intake convention.
    /// </summary>
    [Fact]
    public void DoesNotOverwriteAnExistingLevelProperty()
    {
        var sink = new CapturingSink();
        var logger = new LoggerConfiguration()
            .Enrich.With(new DatadogLogStatusEnricher())
            .WriteTo.Sink(sink)
            .CreateLogger();

        logger.Information("water {level} rising", 42);

        Assert.Equal(42, Assert.IsType<ScalarValue>(sink.LastEvent!.Properties["level"]).Value);
    }
}
