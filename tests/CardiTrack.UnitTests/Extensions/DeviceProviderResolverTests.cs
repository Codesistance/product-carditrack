using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Settings;

namespace CardiTrack.UnitTests.Extensions;

/// <summary>
/// The configured DeviceType→HealthApi mapping every resolution path (worker, sync, connection
/// lifecycle) reads through. Brand names in config are human-typed, so matching must be
/// case-insensitive, and an unmapped brand must resolve to nothing rather than the first block.
/// </summary>
public class DeviceProviderResolverTests
{
    private static List<DeviceProviderSettings> Providers() =>
    [
        new()
        {
            Provider = "GoogleHealth",
            DeviceTypes = ["fitbit", "GooglePixelWatch"],
        },
        new()
        {
            Provider = "SamsungHealth",
            DeviceTypes = ["GalaxyWatch"],
        },
    ];

    [Theory]
    [InlineData(DeviceType.Fitbit, HealthApi.GoogleHealth)]
    [InlineData(DeviceType.GooglePixelWatch, HealthApi.GoogleHealth)]
    [InlineData(DeviceType.GalaxyWatch, HealthApi.SamsungHealth)]
    public void ApiFor_FollowsTheConfiguredMapping(DeviceType deviceType, HealthApi expected)
    {
        Assert.Equal(expected, Providers().ApiFor(deviceType));
    }

    [Fact]
    public void ApiFor_IsNull_ForABrandNoBlockClaims()
    {
        Assert.Null(Providers().ApiFor(DeviceType.Garmin));
    }

    [Fact]
    public void ConfigFor_ReturnsTheClaimingBlock()
    {
        Assert.Equal("SamsungHealth", Providers().ConfigFor(DeviceType.GalaxyWatch)?.Provider);
    }

    [Fact]
    public void Api_IsNull_WhenTheProviderNameIsNotAKnownApi()
    {
        Assert.Null(new DeviceProviderSettings { Provider = "Fitbit" }.Api());
    }
}
