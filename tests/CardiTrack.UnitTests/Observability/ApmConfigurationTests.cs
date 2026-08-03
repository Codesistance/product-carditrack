using CardiTrack.Observability;
using CardiTrack.Observability.Providers;
using Microsoft.Extensions.Configuration;
using Serilog.Events;

namespace CardiTrack.UnitTests.Observability;

public class ApmConfigurationTests
{
    [Fact]
    public void GetApmOptions_BindsEngineAndData()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Apm:Engine"] = "BetterStack",
                ["Apm:Data:IngestUrl"] = "s123.eu-nbg-2.betterstackdata.com",
                ["Apm:Data:IngestToken"] = "token-123",
                ["Apm:MinimumLogLevel"] = "Error",
                ["Apm:TracesSampleRatio"] = "0.5",
            })
            .Build();

        var options = configuration.GetApmOptions();

        Assert.Equal("BetterStack", options.Engine);
        Assert.True(options.IsConfigured);
        Assert.Equal(LogEventLevel.Error, options.ShipLevel);
        Assert.Equal(0.5, options.ClampedSampleRatio);
    }

    [Fact]
    public void GetApmOptions_BindsProviderSpecificExtras_CaseInsensitively()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Apm:Engine"] = "BetterStack",
                ["Apm:Data:IngestUrl"] = "host",
                ["Apm:Data:IngestToken"] = "token",
                ["Apm:Data:Extra:Region"] = "eu-west-1",
                ["Apm:Data:Extra:Dataset"] = "carditrack-dev",
            })
            .Build();

        var options = configuration.GetApmOptions();

        Assert.Equal("eu-west-1", options.Data.Extra["region"]);
        Assert.Equal("carditrack-dev", options.Data.Extra["Dataset"]);
    }

    [Fact]
    public void GetApmOptions_MissingSection_YieldsUnconfiguredDefaults()
    {
        var configuration = new ConfigurationBuilder().Build();

        var options = configuration.GetApmOptions();

        Assert.False(options.IsConfigured);
        Assert.Equal(LogEventLevel.Warning, options.ShipLevel);
        Assert.Equal(0.2, options.ClampedSampleRatio);
    }

    [Theory]
    [InlineData("BetterStack", "", "token")]
    [InlineData("BetterStack", "host", "")]
    [InlineData("", "host", "token")]
    public void IsConfigured_RequiresEngineUrlAndToken(string engine, string url, string token)
    {
        var options = new ApmOptions
        {
            Engine = engine,
            Data = new ApmData { IngestUrl = url, IngestToken = token },
        };

        Assert.False(options.IsConfigured);
    }

    [Theory]
    [InlineData("REPLACE_ME", "token")]
    [InlineData("host", "REPLACE_ME")]
    [InlineData(" REPLACE_ME ", "token")]
    public void IsConfigured_TerraformPlaceholder_CountsAsUnset(string url, string token)
    {
        var options = new ApmOptions
        {
            Engine = "BetterStack",
            Data = new ApmData { IngestUrl = url, IngestToken = token },
        };

        Assert.False(options.IsConfigured);
    }

    [Fact]
    public void ShipLevel_UnparsableValue_FallsBackToWarning()
    {
        var options = new ApmOptions { MinimumLogLevel = "Loud" };

        Assert.Equal(LogEventLevel.Warning, options.ShipLevel);
    }

    [Theory]
    [InlineData(-1.0, 0.0)]
    [InlineData(3.0, 1.0)]
    [InlineData(0.2, 0.2)]
    public void ClampedSampleRatio_StaysWithinBounds(double configured, double expected)
    {
        var options = new ApmOptions { TracesSampleRatio = configured };

        Assert.Equal(expected, options.ClampedSampleRatio);
    }

    [Theory]
    [InlineData("BetterStack")]
    [InlineData("betterstack")]
    public void Registry_ResolvesEngineCaseInsensitively(string engine)
    {
        var provider = ApmProviderRegistry.Resolve(engine);

        Assert.IsType<BetterStackApmProvider>(provider);
    }

    [Fact]
    public void Registry_UnknownEngine_ThrowsListingKnownEngines()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ApmProviderRegistry.Resolve("Datadog"));

        Assert.Contains("Datadog", ex.Message);
        Assert.Contains(BetterStackApmProvider.EngineName, ex.Message);
    }

    [Theory]
    [InlineData("s123.betterstackdata.com", "https://s123.betterstackdata.com")]
    [InlineData("https://s123.betterstackdata.com/", "https://s123.betterstackdata.com")]
    [InlineData(" http://localhost:8080 ", "http://localhost:8080")]
    public void BetterStack_NormalizeIngestUrl_AcceptsHostOrUrl(string input, string expected)
    {
        Assert.Equal(expected, BetterStackApmProvider.NormalizeIngestUrl(input));
    }
}
