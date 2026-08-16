using CardiTrack.Application.Services.Notifications;
using CardiTrack.Mobile.Core.Notifications;

namespace CardiTrack.UnitTests.Mobile;

/// <summary>
/// The foreground copy of a push must land on the same channel the background copy would —
/// otherwise a nudge that happens to arrive while the app is open plays the safety pager
/// instead of the ding, which is the bug this resolver exists to close.
/// </summary>
public class ForegroundChannelResolverTests
{
    [Theory]
    [InlineData("Safety", NotificationChannels.Safety)]
    [InlineData("Health", NotificationChannels.Health)]
    [InlineData("Nudge", NotificationChannels.Nudges)]
    public void KnownCategory_RoutesToItsOwnChannel(string category, string expectedChannel)
    {
        var data = new Dictionary<string, string> { ["category"] = category };

        Assert.Equal(expectedChannel, ForegroundChannelResolver.Resolve(data));
    }

    [Theory]
    [InlineData("Safety")]
    [InlineData("Health")]
    [InlineData("Nudge")]
    public void MatchesWhatTheServerStampsOnTheBackgroundPath(string category)
    {
        // The server sends DeliveryCategory.ToString() in the payload's data — the resolver must
        // round-trip exactly that spelling to the same channel ForCategory picks.
        var parsed = Enum.Parse<CardiTrack.Domain.Enums.DeliveryCategory>(category);
        var data = new Dictionary<string, string> { ["category"] = category };

        Assert.Equal(NotificationChannels.ForCategory(parsed), ForegroundChannelResolver.Resolve(data));
    }

    [Fact]
    public void MissingData_FailsLoudOnSafety()
    {
        Assert.Equal(NotificationChannels.Safety, ForegroundChannelResolver.Resolve(null));
        Assert.Equal(NotificationChannels.Safety, ForegroundChannelResolver.Resolve(new Dictionary<string, string>()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Emergency")] // a category this app version has never heard of
    [InlineData("99")] // numeric parse succeeds but names no defined member
    public void UnreadableCategory_FailsLoudOnSafety(string category)
    {
        var data = new Dictionary<string, string> { ["category"] = category };

        Assert.Equal(NotificationChannels.Safety, ForegroundChannelResolver.Resolve(data));
    }
}
