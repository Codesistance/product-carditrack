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
    public void GetApmOptions_DataAsJsonString_ParsesTheDeploymentForm()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Apm:Engine"] = "BetterStack",
                ["Apm:Data"] = """{"ingestUrl":"s123.betterstackdata.com","INGESTTOKEN":"token-123","Region":"eu"}""",
            })
            .Build();

        var options = configuration.GetApmOptions();

        Assert.True(options.IsConfigured);
        Assert.Equal("s123.betterstackdata.com", options.Data.IngestUrl);
        Assert.Equal("token-123", options.Data.IngestToken);
        Assert.Equal("eu", options.Data.Extra["region"]);
    }

    [Fact]
    public void GetApmOptions_JsonStringWinsOverBoundSection()
    {
        // Deployed reality: appsettings carries the empty nested section, the Apm__Data
        // env var overlays a string value at the same path without removing children.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Apm:Engine"] = "BetterStack",
                ["Apm:Data:IngestUrl"] = "from-section",
                ["Apm:Data:IngestToken"] = "from-section",
            })
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Apm:Data"] = """{"IngestUrl":"from-secret","IngestToken":"secret-token"}""",
            })
            .Build();

        var options = configuration.GetApmOptions();

        Assert.Equal("from-secret", options.Data.IngestUrl);
        Assert.Equal("secret-token", options.Data.IngestToken);
    }

    [Fact]
    public void GetApmOptions_DataAsPlaceholderString_CountsAsUnset()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Apm:Engine"] = "BetterStack",
                ["Apm:Data"] = "REPLACE_ME",
            })
            .Build();

        var options = configuration.GetApmOptions();

        Assert.False(options.IsConfigured);
        Assert.Null(options.Data.IngestUrl);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    public void GetApmOptions_MalformedDataJson_FailsLoudly(string badJson)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Apm:Data"] = badJson })
            .Build();

        var ex = Assert.Throws<InvalidOperationException>(() => configuration.GetApmOptions());

        Assert.Contains("Apm", ex.Message);
    }

    [Fact]
    public void ApmData_FromJson_NestedExtraObjectMerges()
    {
        var data = ApmData.FromJson(
            """{"IngestUrl":"h","IngestToken":"t","Extra":{"Dataset":"carditrack-dev"},"Timeout":30}""");

        Assert.Equal("carditrack-dev", data.Extra["dataset"]);
        Assert.Equal("30", data.Extra["timeout"]);
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
