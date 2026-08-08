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

    /// <summary>
    /// The deployed shape: Terraform writes only Serilog__MinimumLevel__Default per service,
    /// and the sink follows it. Without this, turning a service up widens Cloud Logging while
    /// the APM backend silently stays at Warning.
    /// </summary>
    [Theory]
    [InlineData("Serilog:MinimumLevel:Default", LogEventLevel.Information)]
    [InlineData("Serilog:MinimumLevel", LogEventLevel.Information)]
    public void GetApmOptions_UnpinnedShipLevel_InheritsSerilogRoot(string key, LogEventLevel expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [key] = "Information" })
            .Build();

        Assert.Equal(expected, configuration.GetApmOptions().ShipLevel);
    }

    /// <summary>The object form wins: it is what Terraform sets, over a stale flat value.</summary>
    [Fact]
    public void GetApmOptions_BothSerilogSpellings_PrefersTheObjectForm()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Serilog:MinimumLevel:Default"] = "Debug",
                ["Serilog:MinimumLevel"] = "Error",
            })
            .Build();

        Assert.Equal(LogEventLevel.Debug, configuration.GetApmOptions().ShipLevel);
    }

    /// <summary>
    /// The escape hatch survives inheritance: holding the sink at a stricter level than the
    /// root is how ingest spend gets cut without going dark on the console.
    /// </summary>
    [Fact]
    public void GetApmOptions_ExplicitShipLevel_OverridesSerilogRoot()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Serilog:MinimumLevel:Default"] = "Information",
                ["Apm:MinimumLogLevel"] = "Error",
            })
            .Build();

        Assert.Equal(LogEventLevel.Error, configuration.GetApmOptions().ShipLevel);
    }

    /// <summary>
    /// An empty or placeholder pin is not a pin — it must fall through to the root rather
    /// than reading as a deliberate choice of Warning.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("REPLACE_ME")]
    public void GetApmOptions_BlankOrPlaceholderPin_FallsThroughToSerilogRoot(string pin)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Serilog:MinimumLevel:Default"] = "Information",
                ["Apm:MinimumLogLevel"] = pin,
            })
            .Build();

        Assert.Equal(LogEventLevel.Information, configuration.GetApmOptions().ShipLevel);
    }

    /// <summary>Neither key set — the Warning default still holds.</summary>
    [Fact]
    public void GetApmOptions_NoLevelAnywhere_StaysAtWarning()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Apm:Engine"] = "Datadog" })
            .Build();

        Assert.Equal(LogEventLevel.Warning, configuration.GetApmOptions().ShipLevel);
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
    [InlineData("BetterStack", typeof(BetterStackApmProvider))]
    [InlineData("betterstack", typeof(BetterStackApmProvider))]
    [InlineData("Datadog", typeof(DatadogApmProvider))]
    [InlineData("DATADOG", typeof(DatadogApmProvider))]
    public void Registry_ResolvesEngineCaseInsensitively(string engine, Type expected)
    {
        var provider = ApmProviderRegistry.Resolve(engine);

        Assert.IsType(expected, provider);
    }

    [Fact]
    public void Registry_UnknownEngine_ThrowsListingKnownEngines()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ApmProviderRegistry.Resolve("NewRelic"));

        Assert.Contains("NewRelic", ex.Message);
        Assert.Contains(BetterStackApmProvider.EngineName, ex.Message);
        Assert.Contains(DatadogApmProvider.EngineName, ex.Message);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("false", false)]
    [InlineData("true", true)]
    [InlineData("not-a-bool", false)]
    public void GetApmOptions_MetricsEnabled_BindsAndDefaultsOff(string? value, bool expected)
    {
        var settings = new Dictionary<string, string?> { ["Apm:Engine"] = "Datadog" };
        if (value is not null)
            settings["Apm:MetricsEnabled"] = value;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        Assert.Equal(expected, configuration.GetApmOptions().MetricsEnabled);
    }

    [Theory]
    [InlineData("datadoghq.eu", "https://otlp.datadoghq.eu/v1/metrics")]
    [InlineData("uk1.datadoghq.com", "https://otlp.uk1.datadoghq.com/v1/metrics")]
    [InlineData("us5.datadoghq.com/", "https://otlp.us5.datadoghq.com/v1/metrics")]
    public void Datadog_MetricsIntakeUrl_DerivesFromSite(string site, string expected)
    {
        var options = new ApmOptions { Data = new ApmData { IngestUrl = site } };

        Assert.Equal(expected, DatadogApmProvider.MetricsIntakeUrl(options));
    }

    [Fact]
    public void Datadog_MetricsIntakeUrl_ExplicitEndpointWins()
    {
        var options = new ApmOptions
        {
            Data = new ApmData
            {
                IngestUrl = "datadoghq.eu",
                Extra = { [DatadogApmProvider.MetricsEndpointKey] = "https://otlp.custom.example/v1/metrics" },
            },
        };

        Assert.Equal("https://otlp.custom.example/v1/metrics", DatadogApmProvider.MetricsIntakeUrl(options));
    }

    [Theory]
    [InlineData("https://http-intake.logs.datadoghq.eu")]
    [InlineData("http-intake.logs.datadoghq.eu")]
    [InlineData(null)]
    public void Datadog_MetricsIntakeUrl_UnderivableSite_YieldsNull(string? ingestUrl)
    {
        var options = new ApmOptions { Data = new ApmData { IngestUrl = ingestUrl } };

        Assert.Null(DatadogApmProvider.MetricsIntakeUrl(options));
    }

    [Fact]
    public void Datadog_Describe_WithoutTraceEndpoint_WarnsLogsOnly()
    {
        var options = new ApmOptions
        {
            Engine = "Datadog",
            Data = new ApmData { IngestUrl = "datadoghq.eu", IngestToken = "key" },
        };

        var status = new DatadogApmProvider().Describe(options);

        Assert.Equal("logs", status.Summary);
        Assert.Contains(status.Warnings, w => w.Contains(DatadogApmProvider.TraceEndpointKey));
    }

    [Fact]
    public void Datadog_Describe_FullyConfigured_ShipsAllSignals()
    {
        var options = new ApmOptions
        {
            Engine = "Datadog",
            MetricsEnabled = true,
            Data = new ApmData
            {
                IngestUrl = "uk1.datadoghq.com",
                IngestToken = "key",
                Extra = { [DatadogApmProvider.TraceEndpointKey] = "https://otlp.uk1.datadoghq.com/v1/traces" },
            },
        };

        var status = new DatadogApmProvider().Describe(options);

        Assert.Equal("logs+traces+metrics", status.Summary);
        Assert.Empty(status.Warnings);
    }

    [Fact]
    public void Datadog_Describe_MetricsSwitchedOffIsNotAWarning()
    {
        var options = new ApmOptions
        {
            Engine = "Datadog",
            MetricsEnabled = false,
            Data = new ApmData
            {
                IngestUrl = "datadoghq.eu",
                IngestToken = "key",
                Extra = { [DatadogApmProvider.TraceEndpointKey] = "https://otlp.datadoghq.eu/v1/traces" },
            },
        };

        var status = new DatadogApmProvider().Describe(options);

        Assert.Equal("logs+traces", status.Summary);
        Assert.Empty(status.Warnings);
    }

    [Fact]
    public void Datadog_Describe_MetricsEnabledButUnderivable_Warns()
    {
        var options = new ApmOptions
        {
            Engine = "Datadog",
            MetricsEnabled = true,
            Data = new ApmData { IngestUrl = "https://http-intake.logs.datadoghq.eu", IngestToken = "key" },
        };

        var status = new DatadogApmProvider().Describe(options);

        Assert.DoesNotContain("metrics", status.Signals);
        Assert.Contains(status.Warnings, w => w.Contains(DatadogApmProvider.MetricsEndpointKey));
    }

    [Theory]
    [InlineData(false, "logs+traces")]
    [InlineData(true, "logs+traces+metrics")]
    public void BetterStack_Describe_FollowsMetricsSwitch(bool metricsEnabled, string expected)
    {
        var options = new ApmOptions
        {
            Engine = "BetterStack",
            MetricsEnabled = metricsEnabled,
            Data = new ApmData { IngestUrl = "s123.betterstackdata.com", IngestToken = "token" },
        };

        var status = new BetterStackApmProvider().Describe(options);

        Assert.Equal(expected, status.Summary);
        Assert.Empty(status.Warnings);
    }

    [Theory]
    [InlineData("datadoghq.eu", "https://http-intake.logs.datadoghq.eu")]
    [InlineData("us5.datadoghq.com", "https://http-intake.logs.us5.datadoghq.com")]
    [InlineData("http-intake.logs.datadoghq.com", "https://http-intake.logs.datadoghq.com")]
    [InlineData("https://http-intake.logs.datadoghq.eu/", "https://http-intake.logs.datadoghq.eu")]
    public void Datadog_LogIntakeUrl_DerivesFromSite(string site, string expected)
    {
        Assert.Equal(expected, DatadogApmProvider.LogIntakeUrl(site));
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
