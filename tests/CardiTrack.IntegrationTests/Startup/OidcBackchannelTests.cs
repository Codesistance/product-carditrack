using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using CardiTrack.API.Extensions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CardiTrack.IntegrationTests.Startup;

/// <summary>
/// Pins the outbound budget every JWT Bearer scheme gets for its discovery and JWKS fetches, and
/// that the startup warm-up is wired once for all of them.
/// </summary>
/// <remarks>
/// The defaults — no connect timeout under a 60 s request timeout — are what let a hung first
/// connection to Auth0 turn a sign-in into a minute-long 401 on fresh dev instances. These tests
/// resolve the real options, so they also prove <c>JwtBearerPostConfigureOptions</c> built the
/// back-channel client and the <c>ConfigurationManager</c> from the bounded settings rather than
/// from defaults captured earlier. No network is touched: nothing here fetches a document.
/// </remarks>
public class OidcBackchannelTests
{
    [Theory]
    [InlineData(JwtBearerDefaults.AuthenticationScheme)]
    [InlineData(GoogleOidcExtensions.SchemeName)]
    public void EveryBearerScheme_BoundsItsBackchannel(string scheme)
    {
        using var provider = Provider();
        var options = provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(scheme);

        Assert.Equal(OidcBackchannel.RequestTimeout, options.BackchannelTimeout);
        var handler = Assert.IsType<SocketsHttpHandler>(options.BackchannelHttpHandler);
        Assert.Equal(OidcBackchannel.ConnectTimeout, handler.ConnectTimeout);

        // Without this the runtime's own multi-connect runs, and one dead address of four spends
        // the whole ConnectTimeout before the next is tried.
        Assert.NotNull(handler.ConnectCallback);

        // Built by post-configuration from the two properties above.
        Assert.NotNull(options.Backchannel);
        Assert.Equal(OidcBackchannel.RequestTimeout, options.Backchannel.Timeout);
        Assert.NotNull(options.ConfigurationManager);
    }

    [Fact]
    public void ConnectTimeout_IsShorterThanTheRequestTimeout_WhichIsInsideTheMobileBudget()
    {
        // A stuck connect must fail before the request budget does, and one configuration load
        // (discovery plus JWKS, two requests) must fit inside the mobile client's 30 s.
        Assert.True(OidcBackchannel.ConnectTimeout < OidcBackchannel.RequestTimeout);
        Assert.True(OidcBackchannel.RequestTimeout * 2 < TimeSpan.FromSeconds(30));
    }

    [Theory]
    // One address keeps the whole budget, which is what the runtime already did.
    [InlineData(1, 5000)]
    // The Auth0 tenant's four Cloudflare addresses: a dead one costs a quarter, not the lot.
    [InlineData(4, 1250)]
    // Past the point where an even share would cut off a healthy handshake, the floor holds.
    [InlineData(20, 750)]
    public void PerAddressTimeout_SharesTheBudget_WithoutCuttingOffAHealthyHandshake(
        int addressCount, int expectedMs)
    {
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), OidcBackchannel.PerAddressTimeout(addressCount));
    }

    [Fact]
    public async Task Connect_ReachesALiveAddress_WhenAnEarlierOneIsUnreachable()
    {
        // The dev failure in one test: the first address never answers, and under the runtime's
        // sequential multi-connect it would take every millisecond of ConnectTimeout with it,
        // leaving the live address behind it untried.
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        var port = ((IPEndPoint)listener.LocalEndPoint!).Port;

        var started = Stopwatch.GetTimestamp();
        await using var stream = await OidcBackchannel.ConnectAsync(
            new DnsEndPoint("carditrack-test.invalid", port),
            [Unroutable, IPAddress.Loopback],
            CancellationToken.None);

        Assert.IsType<NetworkStream>(stream);
        Assert.True(
            Stopwatch.GetElapsedTime(started) < OidcBackchannel.ConnectTimeout,
            "the live address must be reached inside the overall connect budget");
    }

    [Fact]
    public async Task Connect_SaysEveryAddressWasTried_WhenNoneAnswer()
    {
        // Port 9 (discard) on unroutable space: nothing to answer on either address. Whether they
        // time out or are refused outright depends on the network the tests run on — either way
        // the loop must exhaust the list and say so, rather than surface one address's error as
        // though it were the whole story.
        var failure = await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await OidcBackchannel.ConnectAsync(
                new DnsEndPoint("carditrack-test.invalid", 9),
                [Unroutable, UnroutableToo],
                CancellationToken.None));

        Assert.Contains("no address accepted a connection", failure.Message);
        Assert.Contains(Unroutable.ToString(), failure.Message);
        Assert.Contains(UnroutableToo.ToString(), failure.Message);
    }

    [Fact]
    public async Task Connect_ReportsHostNotFound_WhenTheIssuerResolvesToNothing()
    {
        // A resolver that answers with an empty set is a DNS failure, and must read as one rather
        // than as "every address was tried" with nothing behind it.
        var failure = await Assert.ThrowsAsync<SocketException>(async () =>
            await OidcBackchannel.ConnectAsync(
                new DnsEndPoint("carditrack-test.invalid", 443), [], CancellationToken.None));

        Assert.Equal(SocketError.HostNotFound, failure.SocketErrorCode);
    }

    [Fact]
    public async Task Connect_StopsImmediately_WhenTheOverallBudgetIsGone()
    {
        // The per-address budget must not become a way to spend more than ConnectTimeout in
        // total: once the outer token is cancelled the loop gives up where it stands.
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await OidcBackchannel.ConnectAsync(
                new DnsEndPoint("carditrack-test.invalid", 9),
                [Unroutable, UnroutableToo],
                cancelled.Token));
    }

    // TEST-NET-1 (RFC 5737): reserved for documentation and not routed, so a SYN to it goes
    // unanswered rather than reaching anything.
    private static readonly IPAddress Unroutable = IPAddress.Parse("192.0.2.1");
    private static readonly IPAddress UnroutableToo = IPAddress.Parse("192.0.2.2");

    [Fact]
    public void DiscoveryWarmup_IsRegisteredOnce_AcrossBothSchemes()
    {
        using var provider = Provider();
        var warmups = provider.GetServices<IHostedService>().OfType<OidcDiscoveryWarmup>();

        Assert.Single(warmups);
    }

    [Fact]
    public async Task DiscoveryWarmup_StartsWithoutWaitingForTheIssuer()
    {
        // The issuer here is unresolvable, so an awaited fetch would sit in the connect timeout.
        // Startup must return regardless: readiness never depends on Auth0.
        using var provider = Provider();
        var warmup = provider.GetServices<IHostedService>().OfType<OidcDiscoveryWarmup>().Single();

        var start = warmup.StartAsync(CancellationToken.None);
        var finished = await Task.WhenAny(start, Task.Delay(TimeSpan.FromSeconds(2)));

        Assert.Same(start, finished);
        await warmup.StopAsync(CancellationToken.None);
    }

    private static ServiceProvider Provider()
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

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuth0Authentication(configuration);
        services.AddGoogleOidcAuthentication(configuration);

        return services.BuildServiceProvider();
    }
}
