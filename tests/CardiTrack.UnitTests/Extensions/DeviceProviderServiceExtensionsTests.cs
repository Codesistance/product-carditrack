using CardiTrack.Infrastructure.Extensions;
using CardiTrack.Infrastructure.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CardiTrack.UnitTests.Extensions;

/// <summary>
/// Startup validation of the per-device-type provider config.
/// </summary>
/// <remarks>
/// These bounds are the only guard on a calibrated pull interval — calibration may move a
/// connection within [min, max] but never outside it. A malformed pair therefore has to stop the
/// host: deployed, it would silently pin every connection of that device type to one end of the
/// range, and the symptom (data arriving late, or a provider rate-limiting us) would surface a
/// long way from the cause.
/// </remarks>
public class DeviceProviderServiceExtensionsTests
{
    [Fact]
    public void AddFitbitProvider_Accepts_AWellFormedConfiguration()
    {
        var settings = Fitbit();

        var exception = Record.Exception(() => Resolve(settings));

        Assert.Null(exception);
    }

    [Fact]
    public void AddFitbitProvider_Throws_WhenFitbitIsNotTheFirstProvider()
    {
        var garmin = Fitbit();
        garmin.Provider = "Garmin";

        var ex = Assert.Throws<InvalidOperationException>(() => Resolve(garmin));

        Assert.Contains("DeviceProviders[0] must be the Fitbit provider", ex.Message);
    }

    [Fact]
    public void AddFitbitProvider_Throws_WhenTheIntervalRangeIsInverted()
    {
        var settings = Fitbit();
        settings.MinPullIntervalMinutes = 240;
        settings.MaxPullIntervalMinutes = 60;

        var ex = Assert.Throws<InvalidOperationException>(() => Resolve(settings));

        Assert.Contains("exceeds MaxPullIntervalMinutes", ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-30)]
    public void AddFitbitProvider_Throws_WhenTheIntervalFloorIsNotPositive(int minutes)
    {
        var settings = Fitbit();
        settings.MinPullIntervalMinutes = minutes;

        var ex = Assert.Throws<InvalidOperationException>(() => Resolve(settings));

        Assert.Contains("must both be greater than zero", ex.Message);
    }

    // A factor of 1 or less never widens the interval, so backoff would be configured and yet do
    // nothing — the failure mode is silence, which is why it is rejected rather than clamped.
    [Fact]
    public void AddFitbitProvider_Throws_WhenBackoffIsEnabledButTheFactorCannotWiden()
    {
        var settings = Fitbit();
        settings.DormancyThresholdPulls = 3;
        settings.DormancyBackoffFactor = 1;

        var ex = Assert.Throws<InvalidOperationException>(() => Resolve(settings));

        Assert.Contains("DormancyBackoffFactor must be", ex.Message);
    }

    // With backoff switched off the factor is unused, so it must not be able to block startup.
    [Fact]
    public void AddFitbitProvider_IgnoresTheBackoffFactor_WhenBackoffIsDisabled()
    {
        var settings = Fitbit();
        settings.DormancyThresholdPulls = 0;
        settings.DormancyBackoffFactor = 1;

        var exception = Record.Exception(() => Resolve(settings));

        Assert.Null(exception);
    }

    private static DeviceProviderSettings Fitbit() => new()
    {
        Provider = "Fitbit",
        ClientId = "test_client",
        ClientSecret = "test_secret",
        TokenUrl = "https://oauth2.googleapis.com/token",
        ApiBaseUrl = "https://health.googleapis.com"
    };

    /// <summary>
    /// Forces the options to materialise, which is what runs the PostConfigure validation.
    /// </summary>
    private static List<DeviceProviderSettings> Resolve(params DeviceProviderSettings[] providers)
    {
        var services = new ServiceCollection();
        services.Configure<List<DeviceProviderSettings>>(list => list.AddRange(providers));
        services.AddFitbitProvider();

        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<List<DeviceProviderSettings>>>().Value;
    }
}
