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
