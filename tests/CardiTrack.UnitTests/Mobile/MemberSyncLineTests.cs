using CardiTrack.Mobile.Core.Family;

namespace CardiTrack.UnitTests.Mobile;

public class MemberSyncLineTests
{
    [Fact]
    public void ARecentSyncSaysWhenAndIsFresh()
    {
        var now = DateTime.UtcNow;

        var (text, freshness) = MemberSyncLine.For(now.AddMinutes(-12), connectedDevices: 1, now);

        Assert.Equal("Last synced 12 minutes ago", text);
        Assert.Equal(SyncFreshness.Recent, freshness);
    }

    [Theory]
    [InlineData(5, SyncFreshness.Quiet)]
    [InlineData(13, SyncFreshness.Gap)]
    public void ALongerSilenceTakesTheDashboardsTiers(int hoursAgo, SyncFreshness expected)
    {
        var now = DateTime.UtcNow;

        Assert.Equal(expected, MemberSyncLine.For(now.AddHours(-hoursAgo), 1, now).Freshness);
    }

    [Fact]
    public void NoDeviceIsSaidPlainly_NotColouredAsAFault()
    {
        Assert.Equal(
            ("No device connected", SyncFreshness.NoDevice),
            MemberSyncLine.For(null, connectedDevices: 0, DateTime.UtcNow));
    }

    [Fact]
    public void AConnectedDeviceThatHasNeverSyncedIsAGap()
    {
        var (text, freshness) = MemberSyncLine.For(null, connectedDevices: 1, DateTime.UtcNow);

        Assert.Equal("Connected — waiting for the first sync", text);
        Assert.Equal(SyncFreshness.Gap, freshness);
    }
}
