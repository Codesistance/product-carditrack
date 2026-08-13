using System.Diagnostics;
using CardiTrack.API.Middleware;
using CardiTrack.Shared.Http;
using Microsoft.AspNetCore.Http;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace CardiTrack.IntegrationTests.Middleware;

/// <summary>
/// The point of the middleware is that a request's telemetry says which build made it, so the
/// cases that matter are the ones where the value silently wouldn't arrive — and, because these
/// headers are unauthenticated input on a public API, the ones where a hostile value would arrive
/// when it shouldn't.
/// </summary>
public class ClientVersionMiddlewareTests
{
    /// <summary>Captures what a logger inside the request actually saw.</summary>
    private sealed class CollectingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    private static DefaultHttpContext BuildContext(string? version, string? platform)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/api/v1/cardimembers";

        if (version is not null)
            context.Request.Headers[ClientHeaderNames.ClientVersion] = version;
        if (platform is not null)
            context.Request.Headers[ClientHeaderNames.ClientPlatform] = platform;

        return context;
    }

    /// <summary>
    /// Runs the middleware over a request and reports what the two telemetry channels received:
    /// the tags left on the ambient span, and the properties visible to a log line written from
    /// inside the request.
    /// </summary>
    private static async Task<(Activity Activity, LogEvent Logged)> RunAsync(HttpContext context)
    {
        var sink = new CollectingSink();
        var logger = BuildLogger(sink);

        // A started Activity with no listener is enough to make Activity.Current non-null, which
        // is all the middleware needs — ASP.NET Core has already started the server Activity by
        // the time middleware runs in the real pipeline.
        using var activity = new Activity("test-request");
        activity.Start();

        var middleware = new ClientVersionMiddleware(_ =>
        {
            logger.Information("inside the request");
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context);

        return (activity, Assert.Single(sink.Events));
    }

    private static Logger BuildLogger(CollectingSink sink) =>
        new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .Enrich.FromLogContext()
            .WriteTo.Sink(sink)
            .CreateLogger();

    private static string? PropertyValue(LogEvent logEvent, string name) =>
        logEvent.Properties.TryGetValue(name, out var value) ? value.ToString().Trim('"') : null;

    [Fact]
    public async Task RecordsVersionAndPlatformOnBothTheSpanAndTheLogs()
    {
        var (activity, logged) = await RunAsync(BuildContext("1.4.2+37", "android"));

        Assert.Equal("1.4.2+37", activity.GetTagItem(ClientVersionMiddleware.VersionTag));
        Assert.Equal("android", activity.GetTagItem(ClientVersionMiddleware.PlatformTag));
        Assert.Equal("1.4.2+37", PropertyValue(logged, ClientVersionMiddleware.VersionProperty));
        Assert.Equal("android", PropertyValue(logged, ClientVersionMiddleware.PlatformProperty));
    }

    /// <summary>
    /// The Web app, every server-to-server caller and every health probe send neither header, and
    /// must not start carrying empty properties because of it.
    /// </summary>
    [Fact]
    public async Task RecordsNothingWhenNeitherHeaderIsSent()
    {
        var (activity, logged) = await RunAsync(BuildContext(version: null, platform: null));

        Assert.Null(activity.GetTagItem(ClientVersionMiddleware.VersionTag));
        Assert.Null(activity.GetTagItem(ClientVersionMiddleware.PlatformTag));
        Assert.Null(PropertyValue(logged, ClientVersionMiddleware.VersionProperty));
        Assert.Null(PropertyValue(logged, ClientVersionMiddleware.PlatformProperty));
    }

    /// <summary>A build that sends only one of the two is still worth recording for that one.</summary>
    [Fact]
    public async Task RecordsWhicheverHeaderIsPresent()
    {
        var (activity, logged) = await RunAsync(BuildContext("1.4.2+37", platform: null));

        Assert.Equal("1.4.2+37", activity.GetTagItem(ClientVersionMiddleware.VersionTag));
        Assert.Null(activity.GetTagItem(ClientVersionMiddleware.PlatformTag));
        Assert.Equal("1.4.2+37", PropertyValue(logged, ClientVersionMiddleware.VersionProperty));
        Assert.Null(PropertyValue(logged, ClientVersionMiddleware.PlatformProperty));
    }

    /// <summary>
    /// Unauthenticated input landing in log lines and span tags. A newline could forge a second
    /// log entry, an over-long value bloats every span it rides on, and a free-text value would
    /// let a caller mint unbounded tag cardinality in the APM backend. All are dropped, not
    /// sanitised — a partially-cleaned version string still reads as a real one.
    /// </summary>
    [Theory]
    [InlineData("1.0\nlevel=fatal forged")]
    [InlineData("1.0; DROP TABLE")]
    [InlineData("../../etc/passwd")]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("")]
    public async Task DropsValuesThatAreNotSafeToRecord(string version)
    {
        var (activity, logged) = await RunAsync(BuildContext(version, platform: null));

        Assert.Null(activity.GetTagItem(ClientVersionMiddleware.VersionTag));
        Assert.Null(PropertyValue(logged, ClientVersionMiddleware.VersionProperty));
    }

    [Fact]
    public async Task DropsAnOverLongValue()
    {
        var (activity, logged) =
            await RunAsync(BuildContext(new string('9', ClientHeaderValues.MaxLength + 1), platform: null));

        Assert.Null(activity.GetTagItem(ClientVersionMiddleware.VersionTag));
        Assert.Null(PropertyValue(logged, ClientVersionMiddleware.VersionProperty));
    }

    /// <summary>
    /// A repeated header gives no way to tell which value was meant. Picking one would let whoever
    /// sent the duplicate decide what gets recorded, so the request is treated as sending none.
    /// </summary>
    [Fact]
    public async Task DropsARepeatedHeader()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[ClientHeaderNames.ClientVersion] = new[] { "1.4.2+37", "9.9.9+1" };

        var (activity, logged) = await RunAsync(context);

        Assert.Null(activity.GetTagItem(ClientVersionMiddleware.VersionTag));
        Assert.Null(PropertyValue(logged, ClientVersionMiddleware.VersionProperty));
    }

    /// <summary>
    /// The properties are pushed for the duration of the request only. Leaving them on the ambient
    /// LogContext would stamp one request's build onto whatever the thread handled next.
    /// </summary>
    [Fact]
    public async Task DoesNotLeakThePropertiesPastTheRequest()
    {
        var sink = new CollectingSink();
        var logger = BuildLogger(sink);

        var middleware = new ClientVersionMiddleware(_ => Task.CompletedTask);
        await middleware.InvokeAsync(BuildContext("1.4.2+37", "android"));

        logger.Information("after the request");

        var logged = Assert.Single(sink.Events);
        Assert.Null(PropertyValue(logged, ClientVersionMiddleware.VersionProperty));
        Assert.Null(PropertyValue(logged, ClientVersionMiddleware.PlatformProperty));
    }

    /// <summary>
    /// Local dev runs with APM unconfigured, where nothing starts a server Activity. The log
    /// properties are the whole point there, so a missing span must not take them down with it.
    /// </summary>
    [Fact]
    public async Task StillLogsWhenThereIsNoAmbientSpan()
    {
        var sink = new CollectingSink();
        var logger = BuildLogger(sink);

        // Cleared explicitly rather than assumed: whether anything upstream has started an
        // Activity is not this test's to depend on, and the point is the null case.
        var previous = Activity.Current;
        Activity.Current = null;
        try
        {
            var middleware = new ClientVersionMiddleware(_ =>
            {
                logger.Information("inside the request");
                return Task.CompletedTask;
            });
            await middleware.InvokeAsync(BuildContext("1.4.2+37", "android"));
        }
        finally
        {
            Activity.Current = previous;
        }

        var logged = Assert.Single(sink.Events);
        Assert.Equal("1.4.2+37", PropertyValue(logged, ClientVersionMiddleware.VersionProperty));
        Assert.Equal("android", PropertyValue(logged, ClientVersionMiddleware.PlatformProperty));
    }
}
