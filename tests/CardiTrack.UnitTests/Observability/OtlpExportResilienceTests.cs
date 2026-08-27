using CardiTrack.Observability;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CardiTrack.UnitTests.Observability;

/// <summary>
/// Pins the two halves of "an export that loses its connection is not a lost batch": a transport
/// that retires pooled connections before the intake does, and a retry behind it.
/// </summary>
public class OtlpExportResilienceTests
{
    /// <summary>
    /// The exporter resolves its transport by name from <see cref="IHttpClientFactory"/>, so the
    /// names are the contract — a typo here is not a compile error, it is a silent fallback to the
    /// SDK's own unconfigured <see cref="HttpClient"/>, which is exactly the state being fixed.
    /// </summary>
    [Theory]
    [InlineData("OtlpTraceExporter")]
    [InlineData("OtlpMetricExporter")]
    public void AddOtlpExportResilience_ConfiguresTheClientTheExporterAsksFor(string name)
    {
        var services = new ServiceCollection();

        services.AddOtlpExportResilience();

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(name);
        Assert.Equal(TimeSpan.FromSeconds(30), client.Timeout);
    }

    [Fact]
    public void EnableExportRetry_TurnsOnInMemoryRetry_WhenNothingHasSaidOtherwise()
    {
        var builder = WebApplication.CreateBuilder();

        OtlpExportResilience.EnableExportRetry(builder.Configuration);

        Assert.Equal("in_memory", builder.Configuration[OtlpExportResilience.RetryConfigurationKey]);
    }

    /// <summary>
    /// An operator who has already chosen — disk-backed retry, or none — keeps their choice. This
    /// is the reason it is written as configuration rather than blindly assigned: a default that
    /// overrode a deliberate setting would be worse than no default.
    /// </summary>
    [Theory]
    [InlineData("disk")]
    [InlineData("none")]
    public void EnableExportRetry_LeavesAnExplicitChoiceAlone(string configured)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [OtlpExportResilience.RetryConfigurationKey] = configured,
        });

        OtlpExportResilience.EnableExportRetry(builder.Configuration);

        Assert.Equal(configured, builder.Configuration[OtlpExportResilience.RetryConfigurationKey]);
    }
}
