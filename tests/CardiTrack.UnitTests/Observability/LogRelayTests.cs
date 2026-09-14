using CardiTrack.Observability;
using CardiTrack.Shared.Http;
using Serilog;
using Serilog.Context;
using Serilog.Core;
using Serilog.Events;

namespace CardiTrack.UnitTests.Observability;

/// <summary>
/// The routing contract between <c>MobileDiagnosticsController</c> (which pushes the property)
/// and <c>DatadogApmProvider</c> (which splits its sinks on it). A relayed event must be exactly
/// one thing — the mobile service's — and an ordinary event must never look relayed.
/// </summary>
public class LogRelayTests
{
    private sealed class CapturingSink : ILogEventSink
    {
        public LogEvent? LastEvent { get; private set; }

        public void Emit(LogEvent logEvent) => LastEvent = logEvent;
    }

    private static LogEvent Emit(Action<ILogger> write)
    {
        var sink = new CapturingSink();
        var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .Enrich.FromLogContext()
            .WriteTo.Sink(sink)
            .CreateLogger();

        write(logger);
        return sink.LastEvent!;
    }

    [Fact]
    public void OrdinaryEvent_IsNotRelayed()
    {
        var logEvent = Emit(logger => logger.Error("HTTP GET /api/v1/alerts responded 500"));

        Assert.False(LogRelay.IsRelayed(logEvent));
        Assert.False(LogRelay.IsRelayedTo(logEvent, ApmServiceNames.Mobile));
    }

    [Fact]
    public void EventCarryingTheMobileService_IsRelayedToMobileAndNothingElse()
    {
        var logEvent = Emit(logger =>
        {
            using (LogContext.PushProperty(LogRelay.ServiceProperty, ApmServiceNames.Mobile))
                logger.Fatal("Mobile Fatal relayed from CardiTrack.Mobile.Unhandled: boom");
        });

        Assert.True(LogRelay.IsRelayed(logEvent));
        Assert.True(LogRelay.IsRelayedTo(logEvent, ApmServiceNames.Mobile));
        Assert.False(LogRelay.IsRelayedTo(logEvent, ApmServiceNames.Api));
    }

    /// <summary>
    /// A property of the right name but the wrong shape (a structure, a number) must not route
    /// anywhere: the provider's mobile sink includes only exact string matches, and the host
    /// sink excludes anything carrying the name, so such an event would otherwise vanish.
    /// </summary>
    [Fact]
    public void NonStringValue_CountsAsRelayedButMatchesNoService()
    {
        var logEvent = Emit(logger =>
        {
            using (LogContext.PushProperty(LogRelay.ServiceProperty, 42))
                logger.Error("odd");
        });

        Assert.True(LogRelay.IsRelayed(logEvent));
        Assert.False(LogRelay.IsRelayedTo(logEvent, ApmServiceNames.Mobile));
    }

    /// <summary>
    /// One name on both ends of the wire: what the app's SDK would call itself, what the relay
    /// files under, and what the runbook tells people to query.
    /// </summary>
    [Fact]
    public void MobileServiceName_IsTheContractsAndNotABackendHost()
    {
        Assert.Equal(MobileDiagnosticsContract.ServiceName, ApmServiceNames.Mobile);
        Assert.Equal("carditrack-mobile", ApmServiceNames.Mobile);
        Assert.DoesNotContain(ApmServiceNames.Mobile, ApmServiceNames.All);
    }
}
