using CardiTrack.Mobile.Core.Offline;

namespace CardiTrack.UnitTests.Mobile;

/// <summary>
/// When a screen should say how old its data is — which is nearly never, because readings arrive
/// every ten minutes and a caption that appears while everything is working cannot mean anything
/// when it stops.
/// </summary>
public class DataAgeTests
{
    private static readonly DateTime Now = new(2026, 9, 18, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(20)]
    [InlineData(29)]
    public void SaysNothing_WhileTheDataIsArrivingAsItShould(int minutesAgo)
    {
        // Ten-minute pulls: everything up to three missed ones is ordinary, including the jitter of
        // a slow provider response, and none of it is worth a line on the screen.
        Assert.False(DataAge.IsWorthShowing(Now.AddMinutes(-minutesAgo), Now));
    }

    [Theory]
    [InlineData(30)]
    [InlineData(45)]
    [InlineData(60 * 4)]
    [InlineData(60 * 26)]
    public void SpeaksUp_OnceThePipelineHasMissed(int minutesAgo)
    {
        Assert.True(DataAge.IsWorthShowing(Now.AddMinutes(-minutesAgo), Now));
    }

    [Fact]
    public void TheThresholdIsThreeMissedPulls()
    {
        // Two would be twenty minutes, which a single slow provider response reaches on its own.
        Assert.Equal(TimeSpan.FromMinutes(30), DataAge.WorthMentioning);
    }

    [Fact]
    public void SaysNothing_ForAMemberWhoHasNeverSynced()
    {
        // Not an age at all, and the screens have their own line for it — "Not synced yet" says
        // something true that "Updated 47 years ago" would not.
        Assert.False(DataAge.IsWorthShowing(null, Now));
    }

    [Fact]
    public void SaysNothing_WhenThePhonesClockIsBehindTheServers()
    {
        // A negative age is a clock disagreeing, not data arriving from the future. The honest
        // reading is that nothing is known to be wrong.
        Assert.False(DataAge.IsWorthShowing(Now.AddMinutes(5), Now));
    }

    [Fact]
    public void TreatsAnUnspecifiedTimestampAsUtc()
    {
        // The API's timestamps deserialize as Unspecified, and reading one as local time would
        // shift the age by the phone's offset — silencing the caption for hours east of UTC and
        // raising it on fresh data west of it.
        var unspecified = new DateTime(2026, 9, 18, 11, 0, 0, DateTimeKind.Unspecified);

        Assert.True(DataAge.IsWorthShowing(unspecified, Now));
    }
}
