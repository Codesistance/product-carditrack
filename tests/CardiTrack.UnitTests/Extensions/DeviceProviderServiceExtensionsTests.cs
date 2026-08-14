using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Infrastructure.Extensions;
using CardiTrack.Infrastructure.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CardiTrack.UnitTests.Extensions;

/// <summary>
/// Startup validation of the per-API provider config, including the DeviceType→HealthApi mapping
/// each block declares through its DeviceTypes list.
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
    public void AddGoogleHealthProvider_Accepts_AWellFormedConfiguration()
    {
        var settings = GoogleHealth();

        var exception = Record.Exception(() => Resolve(settings));

        Assert.Null(exception);
    }

    [Fact]
    public void AddGoogleHealthProvider_Throws_WhenGoogleHealthIsNotTheFirstProvider()
    {
        var garmin = GoogleHealth();
        garmin.Provider = "GarminConnect";
        garmin.DeviceTypes = ["Garmin"];

        var ex = Assert.Throws<InvalidOperationException>(() => Resolve(garmin));

        Assert.Contains("DeviceProviders[0] must be the GoogleHealth provider", ex.Message);
    }

    // The Provider name is the engine key — a typo would leave every connection of the block's
    // device types silently unroutable, so it has to stop the host at startup.
    [Fact]
    public void AddGoogleHealthProvider_Throws_WhenTheProviderIsNotAKnownApi()
    {
        var first = GoogleHealth();
        var second = GoogleHealth();
        second.Provider = "GoogelHealth";
        second.DeviceTypes = ["Garmin"];

        var ex = Assert.Throws<InvalidOperationException>(() => Resolve(first, second));

        Assert.Contains("must name a HealthApi", ex.Message);
    }

    [Fact]
    public void AddGoogleHealthProvider_Throws_WhenABlockServesNoDeviceTypes()
    {
        var settings = GoogleHealth();
        settings.DeviceTypes = [];

        var ex = Assert.Throws<InvalidOperationException>(() => Resolve(settings));

        Assert.Contains("DeviceTypes must list at least", ex.Message);
    }

    [Fact]
    public void AddGoogleHealthProvider_Throws_WhenADeviceTypeNameIsUnknown()
    {
        var settings = GoogleHealth();
        settings.DeviceTypes = ["Fitbit", "PixelWatch"];

        var ex = Assert.Throws<InvalidOperationException>(() => Resolve(settings));

        Assert.Contains("'PixelWatch' is not a DeviceType", ex.Message);
    }

    // First-match resolution would make a double claim an ordering accident — whichever block
    // binds first would silently own the brand's OAuth and sync.
    [Fact]
    public void AddGoogleHealthProvider_Throws_WhenTwoBlocksClaimTheSameDeviceType()
    {
        var first = GoogleHealth();
        var second = GoogleHealth();
        second.Provider = "SamsungHealth";
        second.DeviceTypes = ["GalaxyWatch", "Fitbit"];

        var ex = Assert.Throws<InvalidOperationException>(() => Resolve(first, second));

        Assert.Contains("claimed by both", ex.Message);
    }

    [Fact]
    public void AddGoogleHealthProvider_Throws_WhenTheIntervalRangeIsInverted()
    {
        var settings = GoogleHealth();
        settings.MinPullIntervalMinutes = 240;
        settings.MaxPullIntervalMinutes = 60;

        var ex = Assert.Throws<InvalidOperationException>(() => Resolve(settings));

        Assert.Contains("exceeds MaxPullIntervalMinutes", ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-30)]
    public void AddGoogleHealthProvider_Throws_WhenTheIntervalFloorIsNotPositive(int minutes)
    {
        var settings = GoogleHealth();
        settings.MinPullIntervalMinutes = minutes;

        var ex = Assert.Throws<InvalidOperationException>(() => Resolve(settings));

        Assert.Contains("must both be greater than zero", ex.Message);
    }

    // A factor of 1 or less never widens the interval, so backoff would be configured and yet do
    // nothing — the failure mode is silence, which is why it is rejected rather than clamped.
    [Fact]
    public void AddGoogleHealthProvider_Throws_WhenBackoffIsEnabledButTheFactorCannotWiden()
    {
        var settings = GoogleHealth();
        settings.DormancyThresholdPulls = 3;
        settings.DormancyBackoffFactor = 1;

        var ex = Assert.Throws<InvalidOperationException>(() => Resolve(settings));

        Assert.Contains("DormancyBackoffFactor must be", ex.Message);
    }

    // With backoff switched off the factor is unused, so it must not be able to block startup.
    [Fact]
    public void AddGoogleHealthProvider_IgnoresTheBackoffFactor_WhenBackoffIsDisabled()
    {
        var settings = GoogleHealth();
        settings.DormancyThresholdPulls = 0;
        settings.DormancyBackoffFactor = 1;

        var exception = Record.Exception(() => Resolve(settings));

        Assert.Null(exception);
    }

    /// <summary>
    /// The keyed <c>IDeviceSyncService</c> this extension registers is built by a factory, so a
    /// dependency it cannot resolve is not a startup error — it throws on first use, deep inside
    /// the notification drain, in whichever host happens to lack the registration.
    /// </summary>
    /// <remarks>
    /// That is exactly how it shipped (2026-08-14): <c>INotificationGapResolver</c> lived only in
    /// <c>PushServiceExtensions.AddPushServices</c>, which <c>CardiTrack.PipelineJobs</c>
    /// deliberately does not call, so every notification-triggered sync in that host failed with
    /// "No service for type INotificationGapResolver" while the container validated clean and
    /// Worker — which calls both extensions — stayed healthy. Asserting on this extension alone is
    /// the point: whatever else a host wires, taking the provider must be enough to construct the
    /// sync service it registers.
    /// </remarks>
    [Fact]
    public void AddGoogleHealthProvider_RegistersEverythingItsSyncServiceNeeds_WithoutThePushStack()
    {
        var services = new ServiceCollection();
        services.Configure<List<DeviceProviderSettings>>(list => list.Add(GoogleHealth()));

        services.AddGoogleHealthProvider();

        Assert.Single(services, d => d.ServiceType == typeof(INotificationGapResolver));
        Assert.Single(services, d => d.ServiceType == typeof(INotificationSnapshotQueries));
    }

    private static DeviceProviderSettings GoogleHealth() => new()
    {
        Provider = "GoogleHealth",
        DeviceTypes = ["Fitbit", "GooglePixelWatch"],
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
        services.AddGoogleHealthProvider();

        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<List<DeviceProviderSettings>>>().Value;
    }
}
