using System.Net.Sockets;
using System.Security.Authentication;
using CardiTrack.API.Extensions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace CardiTrack.IntegrationTests.Startup;

/// <summary>
/// The warm-up's retry loop. The first dev instance after PR #514 tried once, hit the connect
/// timeout, and handed the retry to the first caregiver; these pin that it now tries again, stops
/// once a try lands, never holds up startup, and says which leg of the fetch failed. No network is
/// touched: both bearer schemes get a scripted <c>ConfigurationManager</c>, and the back-off runs
/// through a <see cref="TimeProvider"/> that records the pause it was asked for and fires at once.
/// </summary>
public class OidcDiscoveryWarmupTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Retries_UpToThreeTimes_WithTheStatedPauses_WhenTheFetchKeepsFailing()
    {
        var auth0 = ScriptedConfigurationManager.AlwaysFailing();
        var google = ScriptedConfigurationManager.AlwaysSucceeding();
        using var provider = Provider(auth0, google, out var logger, out var time);
        var warmup = Warmup(provider);

        await warmup.StartAsync(CancellationToken.None);
        await warmup.Completion.WaitAsync(WaitLimit);

        Assert.Equal(OidcDiscoveryWarmup.MaxAttempts, auth0.Calls);
        Assert.Equal(1, google.Calls);
        Assert.Equal(OidcDiscoveryWarmup.Backoffs, time.RequestedDelays);

        var attempts = logger.Entries.Where(e => e.Message.Contains("OIDC discovery for Bearer")).ToList();
        Assert.Equal(OidcDiscoveryWarmup.MaxAttempts, attempts.Count);
        Assert.All(attempts, e => Assert.Equal(LogLevel.Warning, e.Level));
        Assert.Contains("attempt 1 of 3", attempts[0].Message);
        Assert.Contains("retrying in 2 s", attempts[0].Message);
        Assert.Contains("attempt 2 of 3", attempts[1].Message);
        Assert.Contains("retrying in 5 s", attempts[1].Message);
        Assert.Contains("attempt 3 of 3", attempts[2].Message);
        Assert.EndsWith("the first authenticated request will try again", attempts[2].Message);
    }

    [Fact]
    public async Task StopsRetrying_OnceAnAttemptSucceeds_AndSaysSoAtWarning()
    {
        var auth0 = ScriptedConfigurationManager.FailingThenSucceeding(failures: 1);
        var google = ScriptedConfigurationManager.AlwaysSucceeding();
        using var provider = Provider(auth0, google, out var logger, out var time);
        var warmup = Warmup(provider);

        await warmup.StartAsync(CancellationToken.None);
        await warmup.Completion.WaitAsync(WaitLimit);

        Assert.Equal(2, auth0.Calls);
        Assert.Equal([OidcDiscoveryWarmup.Backoffs[0]], time.RequestedDelays);

        // Warning, not Information: dev samples Information to 10 %, so the one line per instance
        // that says the warm-up worked has to sit at the level that is actually shipped.
        var success = Assert.Single(logger.Entries, e => e.Message.Contains("for Bearer warmed"));
        Assert.Equal(LogLevel.Warning, success.Level);
        Assert.Contains("on attempt 2 of 3", success.Message);
        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("will try again"));
    }

    [Fact]
    public async Task StartAsync_ReturnsWhileTheFirstAttemptIsStillInFlight()
    {
        // Readiness never depends on the issuer: a fetch that never completes must not hold
        // StartAsync, and StopAsync must be able to end it.
        var auth0 = ScriptedConfigurationManager.NeverCompleting();
        var google = ScriptedConfigurationManager.NeverCompleting();
        using var provider = Provider(auth0, google, out _, out _);
        var warmup = Warmup(provider);

        var start = warmup.StartAsync(CancellationToken.None);
        var finished = await Task.WhenAny(start, Task.Delay(TimeSpan.FromSeconds(2)));

        Assert.Same(start, finished);
        Assert.Equal(1, auth0.Calls);
        Assert.False(warmup.Completion.IsCompleted);

        await warmup.StopAsync(CancellationToken.None);
        await warmup.Completion.WaitAsync(WaitLimit);
    }

    [Fact]
    public async Task StopAsync_EndsTheBackOff_WithoutAnotherAttemptOrAGivingUpLine()
    {
        // Real time here: the point is that a host stopping mid-pause does not sit out the 2 s.
        var auth0 = ScriptedConfigurationManager.AlwaysFailing();
        var google = ScriptedConfigurationManager.AlwaysFailing();
        using var provider = Provider(auth0, google, out var logger, out _, realTime: true);
        var warmup = Warmup(provider);

        await warmup.StartAsync(CancellationToken.None);
        await auth0.WaitForCallAsync(1, WaitLimit);
        await google.WaitForCallAsync(1, WaitLimit);
        await warmup.StopAsync(CancellationToken.None);
        await warmup.Completion.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(1, auth0.Calls);
        Assert.Equal(1, google.Calls);
        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("will try again"));
    }

    [Fact]
    public async Task TheRealConfigurationManager_GoesBackToTheWire_OnEveryAttempt()
    {
        // The retry is only worth having if ConfigurationManager does not hand back its last
        // failure from cache. It caches a configuration, not the absence of one: while it has
        // nothing, every call fetches.
        var retriever = new ThrowingDocumentRetriever();
        var manager = new ConfigurationManager<OpenIdConnectConfiguration>(
            "https://carditrack-test.invalid/.well-known/openid-configuration",
            new OpenIdConnectConfigurationRetriever(),
            retriever);

        for (var attempt = 1; attempt <= OidcDiscoveryWarmup.MaxAttempts; attempt++)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => manager.GetConfigurationAsync(CancellationToken.None));
            Assert.Equal(attempt, retriever.Calls);
        }
    }

    [Theory]
    [MemberData(nameof(Failures))]
    public void DescribeFailure_NamesTheLegThatGaveWay(Exception thrown, TimeSpan elapsed, string expected)
    {
        var description = OidcDiscoveryWarmup.DescribeFailure(thrown, elapsed);

        Assert.Contains(expected, description);
    }

    public static TheoryData<Exception, TimeSpan, string> Failures()
    {
        // Every case is wrapped the way ConfigurationManager wraps it: IDX20803 on the outside,
        // the transport's exception somewhere inside.
        static Exception Idx20803(Exception inner) =>
            new InvalidOperationException("IDX20803: Unable to obtain configuration from: 'https://carditrack-test.invalid/'.", inner);

        var connectTimeout = OidcBackchannel.ConnectTimeout + TimeSpan.FromMilliseconds(100);
        var requestTimeout = OidcBackchannel.RequestTimeout + TimeSpan.FromMilliseconds(200);

        return new TheoryData<Exception, TimeSpan, string>
        {
            {
                Idx20803(new HttpRequestException("No such host is known.", new SocketException((int)SocketError.HostNotFound))),
                TimeSpan.FromMilliseconds(30),
                "SocketException: DNS lookup failed (HostNotFound)"
            },
            {
                Idx20803(new HttpRequestException("Connection refused", new SocketException((int)SocketError.ConnectionRefused))),
                TimeSpan.FromMilliseconds(30),
                "SocketException: TCP connect failed (ConnectionRefused)"
            },
            {
                Idx20803(new HttpRequestException("The SSL connection could not be established", new AuthenticationException("handshake"))),
                TimeSpan.FromMilliseconds(300),
                "AuthenticationException: TLS handshake failed"
            },
            {
                Idx20803(new TaskCanceledException("A connection could not be established within the configured ConnectTimeout.", new TimeoutException("timeout"))),
                connectTimeout,
                "TimeoutException: timed out in the connect phase"
            },
            {
                Idx20803(new HttpRequestException("connect", new OperationCanceledException("cancelled"))),
                connectTimeout,
                "OperationCanceledException: timed out in the connect phase"
            },
            {
                Idx20803(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 10 seconds elapsing.", new TimeoutException("timeout"))),
                requestTimeout,
                "TimeoutException: timed out waiting for the response"
            },
            {
                Idx20803(new HttpRequestException(HttpRequestError.ProxyTunnelError, "proxy")),
                TimeSpan.FromMilliseconds(30),
                "HttpRequestException: ProxyTunnelError"
            },
            {
                Idx20803(new IOException("IDX20807: Could not retrieve document")),
                TimeSpan.FromMilliseconds(30),
                "IOException: IDX20807"
            },
        };
    }

    private static OidcDiscoveryWarmup Warmup(ServiceProvider provider) =>
        provider.GetServices<IHostedService>().OfType<OidcDiscoveryWarmup>().Single();

    private static ServiceProvider Provider(
        ScriptedConfigurationManager auth0,
        ScriptedConfigurationManager google,
        out ListLogger logger,
        out RecordingTimeProvider time,
        bool realTime = false)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth0:Domain"] = "carditrack-test.invalid",
                ["Auth0:Audience"] = "https://api.carditrack.test",
                ["Pipeline:Audience"] = "carditrack-test-internal-notifications",
                ["Pipeline:ServiceAccount"] = "pipeline-sa@carditrack-test.iam.gserviceaccount.com",
            })
            .Build();

        logger = new ListLogger();
        time = new RecordingTimeProvider();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ILogger<OidcDiscoveryWarmup>>(logger);
        if (!realTime)
            services.AddSingleton<TimeProvider>(time);
        services.AddAuth0Authentication(configuration);
        services.AddGoogleOidcAuthentication(configuration);

        // JwtBearerPostConfigureOptions only builds a ConfigurationManager when none is set, so a
        // scripted one from Configure survives post-configuration and is what the warm-up resolves.
        services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o => o.ConfigurationManager = auth0);
        services.Configure<JwtBearerOptions>(GoogleOidcExtensions.SchemeName, o => o.ConfigurationManager = google);

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Plays a fixed script of outcomes, one per call, and counts the calls. A script that runs out
    /// repeats its last entry.
    /// </summary>
    private sealed class ScriptedConfigurationManager(Func<int, CancellationToken, Task<OpenIdConnectConfiguration>> script)
        : IConfigurationManager<OpenIdConnectConfiguration>
    {
        private static readonly InvalidOperationException Failure = new(
            "IDX20803: Unable to obtain configuration from: 'https://carditrack-test.invalid/'.",
            new TaskCanceledException(
                "A connection could not be established within the configured ConnectTimeout.",
                new TimeoutException("timeout")));

        private int _calls;
        private readonly SemaphoreSlim _called = new(0);

        public int Calls => Volatile.Read(ref _calls);

        public static ScriptedConfigurationManager AlwaysFailing() =>
            new(static (_, _) => Task.FromException<OpenIdConnectConfiguration>(Failure));

        public static ScriptedConfigurationManager AlwaysSucceeding() =>
            new(static (_, _) => Task.FromResult(new OpenIdConnectConfiguration()));

        public static ScriptedConfigurationManager FailingThenSucceeding(int failures) =>
            new((call, _) => call <= failures
                ? Task.FromException<OpenIdConnectConfiguration>(Failure)
                : Task.FromResult(new OpenIdConnectConfiguration()));

        public static ScriptedConfigurationManager NeverCompleting() =>
            new(static (_, ct) =>
            {
                var pending = new TaskCompletionSource<OpenIdConnectConfiguration>();
                ct.Register(() => pending.TrySetCanceled(ct));
                return pending.Task;
            });

        public Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel)
        {
            var call = Interlocked.Increment(ref _calls);
            _called.Release();
            return script(call, cancel);
        }

        public void RequestRefresh()
        {
        }

        public async Task WaitForCallAsync(int count, TimeSpan limit)
        {
            using var timeout = new CancellationTokenSource(limit);
            while (Calls < count)
                await _called.WaitAsync(timeout.Token);
        }
    }

    /// <summary>Fails every document fetch, the way an unreachable issuer does under the real retriever.</summary>
    private sealed class ThrowingDocumentRetriever : IDocumentRetriever
    {
        public int Calls { get; private set; }

        public Task<string> GetDocumentAsync(string address, CancellationToken cancel)
        {
            Calls++;
            throw new HttpRequestException("A connection could not be established within the configured ConnectTimeout.");
        }
    }

    /// <summary>
    /// Records what each <c>Task.Delay</c> asked to wait for and then fires it immediately, so the
    /// schedule is asserted rather than slept through.
    /// </summary>
    private sealed class RecordingTimeProvider : TimeProvider
    {
        private readonly List<TimeSpan> _requestedDelays = new();

        public IReadOnlyList<TimeSpan> RequestedDelays
        {
            get { lock (_requestedDelays) return _requestedDelays.ToList(); }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_requestedDelays) _requestedDelays.Add(dueTime);
            return base.CreateTimer(callback, state, TimeSpan.Zero, period);
        }
    }

    /// <summary>Hand-rolled recording logger, matching the suite's no-mocking-library style.</summary>
    private sealed class ListLogger : ILogger<OidcDiscoveryWarmup>
    {
        private readonly List<(LogLevel Level, string Message)> _entries = new();

        public IReadOnlyList<(LogLevel Level, string Message)> Entries
        {
            get { lock (_entries) return _entries.ToList(); }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_entries) _entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
