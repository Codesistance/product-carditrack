using CardiTrack.Application.Services.Notifications;
using CardiTrack.Domain.Enums;

namespace CardiTrack.UnitTests.Notifications;

/// <summary>
/// The category→channel and category→sound mappings have three consumers that must agree:
/// <c>FcmNotificationChannel</c> stamping the payload, <c>PushRegistrationCoordinator</c> creating
/// the OS channels, and the dev test-push endpoint reporting what it just sent. A disagreement is
/// invisible — the push arrives, silently, on a channel nobody configured — so the mapping lives
/// in one place and this pins it.
/// </summary>
public class NotificationChannelMappingTests
{
    [Theory]
    [InlineData(DeliveryCategory.Safety, NotificationChannels.Safety)]
    [InlineData(DeliveryCategory.Health, NotificationChannels.Health)]
    [InlineData(DeliveryCategory.Nudge, NotificationChannels.Nudges)]
    public void ForCategory_MapsEachCategoryToItsChannel(DeliveryCategory category, string expected)
    {
        Assert.Equal(expected, NotificationChannels.ForCategory(category));
    }

    [Theory]
    [InlineData(DeliveryCategory.Safety, NotificationChannels.AlertSound)]
    [InlineData(DeliveryCategory.Health, NotificationChannels.AlertSound)]
    [InlineData(DeliveryCategory.Nudge, NotificationChannels.NudgeSound)]
    public void AndroidSoundFor_SharesTheAlertChimeAcrossSafetyAndHealth(
        DeliveryCategory category, string expected)
    {
        Assert.Equal(expected, NotificationChannels.AndroidSoundFor(category));
    }

    [Theory]
    [InlineData(DeliveryCategory.Safety, NotificationChannels.AlertSoundFile)]
    [InlineData(DeliveryCategory.Health, NotificationChannels.AlertSoundFile)]
    [InlineData(DeliveryCategory.Nudge, NotificationChannels.NudgeSoundFile)]
    public void IosSoundFor_NamesTheBundleFile(DeliveryCategory category, string expected)
    {
        Assert.Equal(expected, NotificationChannels.IosSoundFor(category));
    }

    [Fact]
    public void AndroidAndIosSoundsAreTheSameAssetPerCategory()
    {
        // The two platforms name the same file differently — Android by resource name, iOS by
        // bundle filename. AlertSoundAssetTests already proves the bytes match; this proves the
        // two lookups can't drift onto different assets.
        foreach (var category in Enum.GetValues<DeliveryCategory>())
        {
            Assert.Equal(
                NotificationChannels.AndroidSoundFor(category) + ".wav",
                NotificationChannels.IosSoundFor(category));
        }
    }
}
