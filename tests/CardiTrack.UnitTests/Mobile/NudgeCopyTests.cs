using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Core.Notifications;

namespace CardiTrack.UnitTests.Mobile;

public class NudgeCopyTests
{
    private static NotificationResponse Reconnect(string templateData) => new()
    {
        RuleCode = "DEVICE_AUTH_BROKEN",
        TitleKey = "nudge.DEVICE_AUTH_BROKEN.expired.title",
        BodyKey = "nudge.DEVICE_AUTH_BROKEN.expired.body",
        CardiMemberName = "Pop Smith",
        CardiMemberFirstName = "Pop",
        TemplateData = templateData,
    };

    [Fact]
    public void TheReconnectReminder_NamesTheDevice()
    {
        Assert.Equal("Pop's Fitbit needs reconnecting", NudgeCopy.Title(Reconnect("""{"device":"Fitbit"}""")));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("")]
    public void AReminderRaisedBeforeTheDeviceWasRecorded_SaysWatch(string templateData)
    {
        var n = Reconnect(templateData);

        Assert.Equal("Pop's watch needs reconnecting", NudgeCopy.Title(n));
        Assert.DoesNotContain("{device}", NudgeCopy.Body(n));
    }
}
