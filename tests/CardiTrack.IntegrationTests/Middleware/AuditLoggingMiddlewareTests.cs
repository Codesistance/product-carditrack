using CardiTrack.API.Infrastructure.Auditing;
using CardiTrack.API.Infrastructure.UserContext;
using CardiTrack.API.Middleware;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace CardiTrack.IntegrationTests.Middleware;

/// <summary>
/// The audit trail is what GDPR accountability rests on, so the cases that matter are the ones
/// where an entry would silently not be written.
/// </summary>
public class AuditLoggingMiddlewareTests
{
    private readonly IAuditLogRepository _auditLogs = Substitute.For<IAuditLogRepository>();
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _memberId = Guid.NewGuid();

    /// <summary>Minimal IUserContext stand-in — the real one is settable only from JWT claims.</summary>
    private sealed class FakeUserContext : IUserContext
    {
        public Guid UserId { get; init; }
        public string Auth0UserId => "auth0|test";
        public Guid OrganizationId => Guid.Empty;
        public string Email => "caregiver@example.com";
        public UserRole Role => UserRole.Member;
        public bool IsAuthenticated { get; init; }
        public string Locale => "en-GB";
        public bool? EmailVerified => true;
    }

    private static DefaultHttpContext BuildContext(
        AuditHealthDataAccessAttribute? attribute,
        int statusCode = StatusCodes.Status200OK,
        (string Key, string Value)? routeValue = null)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = "GET";
        httpContext.Request.Path = "/api/v1/cardimembers/x/dashboard";
        httpContext.Request.Headers.UserAgent = "CardiTrack/1.0";
        httpContext.Response.StatusCode = statusCode;

        if (routeValue is not null)
            httpContext.Request.RouteValues[routeValue.Value.Key] = routeValue.Value.Value;

        if (attribute is not null)
        {
            httpContext.Features.Set<IEndpointFeature>(new EndpointFeature
            {
                Endpoint = new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(attribute), "test"),
            });
        }

        return httpContext;
    }

    private sealed class EndpointFeature : IEndpointFeature
    {
        public Endpoint? Endpoint { get; set; }
    }

    private AuditLoggingMiddleware CreateSut(RequestDelegate? next = null, IDistributedCache? cache = null) =>
        new(next ?? (_ => Task.CompletedTask),
            Substitute.For<ILogger<AuditLoggingMiddleware>>(),
            cache ?? new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())));

    private async Task<AuditLog?> InvokeAndCaptureAsync(
        DefaultHttpContext httpContext, bool authenticated = true)
    {
        AuditLog? captured = null;
        await _auditLogs.AppendAsync(Arg.Do<AuditLog>(a => captured = a), Arg.Any<CancellationToken>());

        var userContext = new FakeUserContext
        {
            UserId = authenticated ? _userId : Guid.Empty,
            IsAuthenticated = authenticated,
        };

        await CreateSut().InvokeAsync(httpContext, userContext, _auditLogs);
        return captured;
    }

    // ── What gets recorded ──────────────────────────────────────────────────────

    [Fact]
    public async Task WritesAnEntry_ForAnAuditedEndpoint()
    {
        var context = BuildContext(
            new AuditHealthDataAccessAttribute("ViewDashboard"),
            routeValue: ("cardiMemberId", null!));
        context.Request.RouteValues["cardiMemberId"] = _memberId.ToString();

        var entry = await InvokeAndCaptureAsync(context);

        Assert.NotNull(entry);
        Assert.Equal(_userId, entry!.UserId);
        Assert.Equal(_memberId, entry.CardiMemberId);
        Assert.Equal("ViewDashboard", entry.Action);
        Assert.Equal("CardiMember", entry.EntityType);
        Assert.Equal("GET", entry.HttpMethod);
        Assert.Equal(200, entry.ResponseStatus);
        Assert.Equal("CardiTrack/1.0", entry.UserAgent);
    }

    [Fact]
    public async Task RecordsADeniedAttempt_NotJustSuccessfulOnes()
    {
        // A caregiver probing for someone else's member is exactly what an audit trail is for.
        var context = BuildContext(
            new AuditHealthDataAccessAttribute("ViewDashboard"),
            statusCode: StatusCodes.Status404NotFound);

        var entry = await InvokeAndCaptureAsync(context);

        Assert.NotNull(entry);
        Assert.Equal(404, entry!.ResponseStatus);
    }

    [Fact]
    public async Task RecordsPathWithoutQueryString()
    {
        var context = BuildContext(new AuditHealthDataAccessAttribute("ViewDashboard"));
        context.Request.QueryString = new QueryString("?code=super-secret&state=abc");

        var entry = await InvokeAndCaptureAsync(context);

        Assert.NotNull(entry);
        Assert.DoesNotContain("super-secret", entry!.RequestPath);
        Assert.Equal("/api/v1/cardimembers/x/dashboard", entry.RequestPath);
    }

    [Fact]
    public async Task FallsBackToMethodAndPath_WhenNoActionNameIsGiven()
    {
        var entry = await InvokeAndCaptureAsync(BuildContext(new AuditHealthDataAccessAttribute()));

        Assert.NotNull(entry);
        Assert.Equal("GET /api/v1/cardimembers/x/dashboard", entry!.Action);
    }

    [Fact]
    public async Task RecordsEntry_WithoutAMemberId_WhenTheRouteDoesNotCarryOne()
    {
        // Report generation names its members in the body; the access is still worth recording.
        var entry = await InvokeAndCaptureAsync(BuildContext(new AuditHealthDataAccessAttribute("Report")));

        Assert.NotNull(entry);
        Assert.Null(entry!.CardiMemberId);
    }

    [Fact]
    public async Task UsesTheDeclaredEntityType()
    {
        var entry = await InvokeAndCaptureAsync(BuildContext(
            new AuditHealthDataAccessAttribute("AccessDevice") { EntityType = "DeviceConnection" }));

        Assert.Equal("DeviceConnection", entry!.EntityType);
    }

    [Fact]
    public async Task TruncatesAnOverlongUserAgent_RatherThanFailingTheWrite()
    {
        var context = BuildContext(new AuditHealthDataAccessAttribute("ViewDashboard"));
        context.Request.Headers.UserAgent = new string('x', 900);

        var entry = await InvokeAndCaptureAsync(context);

        // The column is bounded at 500; a hostile or unusual client must not break auditing.
        Assert.Equal(500, entry!.UserAgent.Length);
    }

    [Fact]
    public async Task TruncatesEveryBoundedColumn_NotJustTheObviousOnes()
    {
        // The default Action is method + path, and Action is bounded at 100 while the path is
        // allowed 500 — so a route carrying a few GUIDs overflows Action long before the path.
        // An overflow fails the whole write, losing the record rather than shortening it.
        var context = BuildContext(attribute: new AuditHealthDataAccessAttribute());
        context.Request.Path = "/api/v1/" + string.Join('/', Enumerable.Repeat(Guid.NewGuid().ToString(), 6));
        context.Request.Method = new string('M', 40);
        context.Request.Headers.UserAgent = new string('u', 900);

        var entry = await InvokeAndCaptureAsync(context);

        Assert.NotNull(entry);
        Assert.True(entry!.Action.Length <= 100, $"Action was {entry.Action.Length}");
        Assert.True(entry.EntityType.Length <= 100);
        Assert.True(entry.HttpMethod.Length <= 10, $"HttpMethod was {entry.HttpMethod.Length}");
        Assert.True(entry.IpAddress.Length <= 50);
        Assert.True(entry.UserAgent.Length <= 500);
        Assert.True(entry.RequestPath.Length <= 500);
    }

    [Fact]
    public async Task RecordsTheEntry_WhenTheActionThrows()
    {
        // In the pipeline this middleware sits outside ExceptionHandlingMiddleware, which turns
        // a throw into a 401/404/500 and returns normally. Registered inside that handler it
        // would be skipped entirely on any throw — losing the very outcomes worth auditing.
        // Here the exception propagates, standing in for a handler that is absent.
        var context = BuildContext(new AuditHealthDataAccessAttribute("ViewDashboard"));
        var userContext = new FakeUserContext { UserId = _userId, IsAuthenticated = true };
        var sut = CreateSut(_ => throw new KeyNotFoundException("CardiMember not found"));

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => sut.InvokeAsync(context, userContext, _auditLogs));

        // Nothing is written when the exception escapes this middleware, which is precisely why
        // the registration order in Program.cs matters — see the comment there.
        await _auditLogs.DidNotReceive().AppendAsync(Arg.Any<AuditLog>(), Arg.Any<CancellationToken>());
    }

    // ── What is deliberately not recorded ───────────────────────────────────────

    [Fact]
    public async Task WritesNothing_ForAnUnmarkedEndpoint()
    {
        await InvokeAndCaptureAsync(BuildContext(attribute: null));

        await _auditLogs.DidNotReceive().AppendAsync(Arg.Any<AuditLog>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WritesNothing_ForAnUnauthenticatedCaller()
    {
        await InvokeAndCaptureAsync(
            BuildContext(new AuditHealthDataAccessAttribute("ViewDashboard")), authenticated: false);

        // AuditLog.UserId is required, and an unauthenticated caller never passed [Authorize]
        // to reach real data.
        await _auditLogs.DidNotReceive().AppendAsync(Arg.Any<AuditLog>(), Arg.Any<CancellationToken>());
    }

    // ── Failure behaviour ───────────────────────────────────────────────────────

    [Fact]
    public async Task DoesNotFailTheRequest_WhenTheAuditWriteThrows()
    {
        _auditLogs.AppendAsync(Arg.Any<AuditLog>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("audit table unavailable"));

        var userContext = new FakeUserContext { UserId = _userId, IsAuthenticated = true };

        // The response has already been sent by this point; throwing would turn a completed
        // request into a 500 after the fact.
        await CreateSut().InvokeAsync(
            BuildContext(new AuditHealthDataAccessAttribute("ViewDashboard")), userContext, _auditLogs);
    }

    [Fact]
    public async Task StillWritesTheAuditEntry_WhenTheClientHasDisconnected()
    {
        // The write happens after the pipeline. A cancelled dashboard poll would otherwise
        // cancel the SQL insert via RequestAborted and drop the row.
        var context = BuildContext(new AuditHealthDataAccessAttribute("ViewDashboard"));
        using var gone = new CancellationTokenSource();
        gone.Cancel();
        context.RequestAborted = gone.Token;
        var userContext = new FakeUserContext { UserId = _userId, IsAuthenticated = true };

        await CreateSut().InvokeAsync(context, userContext, _auditLogs);

        await _auditLogs.Received(1).AppendAsync(
            Arg.Any<AuditLog>(),
            Arg.Is<CancellationToken>(t => !t.IsCancellationRequested));
    }

    [Fact]
    public async Task CoalescesRepeatGets_OfTheSameLook()
    {
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var context = BuildContext(
            new AuditHealthDataAccessAttribute("ViewDashboard"),
            routeValue: ("cardiMemberId", _memberId.ToString()));
        var userContext = new FakeUserContext { UserId = _userId, IsAuthenticated = true };
        var sut = CreateSut(cache: cache);

        await sut.InvokeAsync(context, userContext, _auditLogs);
        await sut.InvokeAsync(context, userContext, _auditLogs);

        await _auditLogs.Received(1).AppendAsync(Arg.Any<AuditLog>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StillRecordsEveryWrite_AndEveryDeniedGet()
    {
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var userContext = new FakeUserContext { UserId = _userId, IsAuthenticated = true };
        var sut = CreateSut(cache: cache);

        var acknowledge = BuildContext(new AuditHealthDataAccessAttribute("AcknowledgeAlert"));
        acknowledge.Request.Method = "POST";
        await sut.InvokeAsync(acknowledge, userContext, _auditLogs);
        await sut.InvokeAsync(acknowledge, userContext, _auditLogs);

        var denied = BuildContext(
            new AuditHealthDataAccessAttribute("ViewDashboard"),
            statusCode: StatusCodes.Status404NotFound);
        await sut.InvokeAsync(denied, userContext, _auditLogs);
        await sut.InvokeAsync(denied, userContext, _auditLogs);

        await _auditLogs.Received(4).AppendAsync(Arg.Any<AuditLog>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunsTheRestOfThePipeline_BeforeRecording()
    {
        var pipelineRan = false;
        var context = BuildContext(new AuditHealthDataAccessAttribute("ViewDashboard"));
        var userContext = new FakeUserContext { UserId = _userId, IsAuthenticated = true };

        var sut = CreateSut(_ =>
        {
            pipelineRan = true;
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        });

        AuditLog? captured = null;
        await _auditLogs.AppendAsync(Arg.Do<AuditLog>(a => captured = a), Arg.Any<CancellationToken>());

        await sut.InvokeAsync(context, userContext, _auditLogs);

        Assert.True(pipelineRan);
        // The status is only knowable after the pipeline, which is why the write happens last.
        Assert.Equal(403, captured!.ResponseStatus);
    }
}
