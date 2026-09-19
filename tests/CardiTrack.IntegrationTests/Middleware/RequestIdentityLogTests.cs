using CardiTrack.API.Infrastructure.Logging;
using CardiTrack.API.Infrastructure.UserContext;
using CardiTrack.API.Middleware;
using CardiTrack.Domain.Enums;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;

namespace CardiTrack.IntegrationTests.Middleware;

/// <summary>
/// The 500s on <c>POST /api/v1/notifications/devices</c> that started this could be counted in
/// Datadog but not attributed: the line naming the fault is written by
/// <see cref="ExceptionHandlingMiddleware"/>, which sits *outside* <c>UserContextMiddleware</c>,
/// so no property that middleware pushes onto Serilog's <c>LogContext</c> ever reaches it. These
/// pin that the identity now arrives there, and that a hostile device id does not.
/// </summary>
public class RequestIdentityLogTests
{
    private sealed class CollectingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    private static Logger BuildLogger(CollectingSink sink) =>
        new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .Enrich.FromLogContext()
            .WriteTo.Sink(sink)
            .CreateLogger();

    private static DefaultHttpContext BuildContext(Guid? userId)
    {
        var services = new ServiceCollection();

        var userContext = new UserContext();
        if (userId is { } id)
        {
            userContext.SetAuthenticatedUser("auth0|test", "caregiver@example.com", "en-GB");
            userContext.SetFullUserContext(id, Guid.NewGuid(), UserRole.Member);
        }

        services.AddSingleton<IUserContext>(userContext);

        return new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            Request = { Method = "POST", Path = "/api/v1/notifications/devices" }
        };
    }

    /// <summary>Runs a throwing pipeline through the handler and returns the line it wrote.</summary>
    private static async Task<LogEvent> RunFailingRequestAsync(HttpContext context)
    {
        var sink = new CollectingSink();
        using var logger = BuildLogger(sink);
        using var factory = new SerilogLoggerFactory(logger);

        var middleware = new ExceptionHandlingMiddleware(
            _ => throw new InvalidOperationException("something in the database said no"),
            LoggerFactoryExtensions.CreateLogger<ExceptionHandlingMiddleware>(factory),
            new TestHostEnvironment());

        context.Response.Body = new MemoryStream();
        await middleware.InvokeAsync(context);

        return Assert.Single(sink.Events);
    }

    private static string? PropertyValue(LogEvent logEvent, string name) =>
        logEvent.Properties.TryGetValue(name, out var value) ? value.ToString().Trim('"') : null;

    [Fact]
    public async Task AFaultNamesTheUserAndTheDevice()
    {
        var userId = Guid.NewGuid();
        var context = BuildContext(userId);
        RequestIdentityLog.RecordDeviceId(context, "8d1f4c2b9a6e47d3b05f1c8e2a7d6493");

        var logged = await RunFailingRequestAsync(context);

        Assert.Equal(userId.ToString(), PropertyValue(logged, RequestIdentityLog.UserIdProperty));
        Assert.Equal("8d1f4c2b9a6e47d3b05f1c8e2a7d6493", PropertyValue(logged, RequestIdentityLog.DeviceIdProperty));
    }

    [Fact]
    public async Task AnUnauthenticatedFaultCarriesNeitherRatherThanAnEmptyOne()
    {
        // Sign-in, onboarding and every health probe reach the handler with no database identity.
        // A "UserId" of Guid.Empty on those lines would read as a real account.
        var logged = await RunFailingRequestAsync(BuildContext(userId: null));

        Assert.Null(PropertyValue(logged, RequestIdentityLog.UserIdProperty));
        Assert.Null(PropertyValue(logged, RequestIdentityLog.DeviceIdProperty));
    }

    [Theory]
    [InlineData("device\nStatusCode: 200 — forged")]
    [InlineData("device id with spaces")]
    [InlineData("")]
    public void ADeviceIdThatCouldForgeALogLineIsNotRecorded(string deviceId)
    {
        // The value is client-supplied on an endpoint that any installed app can call.
        var context = BuildContext(Guid.NewGuid());

        Assert.Null(RequestIdentityLog.RecordDeviceId(context, deviceId));
        Assert.Null(RequestIdentityLog.DeviceId(context));
    }

    [Fact]
    public void AnOverLongDeviceIdIsRefusedRatherThanTruncated()
    {
        // DeviceId is varchar(128) in the database but only ever a 32-character hex GUID from our
        // own client. Clipping an over-long one would put something that reads like a real device
        // id on a log line.
        var context = BuildContext(Guid.NewGuid());

        Assert.Null(RequestIdentityLog.RecordDeviceId(context, new string('a', 129)));
        Assert.Null(RequestIdentityLog.DeviceId(context));
    }

    /// <summary>Production, so the handler never adds its development-only exception detail.</summary>
    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "CardiTrack.API.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
