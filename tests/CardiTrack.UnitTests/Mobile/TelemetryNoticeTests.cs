using CardiTrack.Mobile.Core.Diagnostics;

namespace CardiTrack.UnitTests.Mobile;

/// <summary>
/// The telemetry notice is remembered per caregiver, and its copy has to send people to the
/// switch the app actually has.
/// </summary>
public class TelemetryNoticeTests
{
    [Fact]
    public void IsSeen_OnlyForTheCaregiverWhoAcknowledgedIt()
    {
        var stored = TelemetryNotice.SeenValueFor("ada@example.com");

        Assert.True(TelemetryNotice.IsSeen(stored, "ada@example.com"));
        Assert.True(TelemetryNotice.IsSeen(stored, "  ADA@example.com "));
        Assert.False(TelemetryNotice.IsSeen(stored, "grace@example.com"));
    }

    [Fact]
    public void IsSeen_FalseWhenNothingStoredOrNoIdentity()
    {
        Assert.False(TelemetryNotice.IsSeen(null, "ada@example.com"));
        Assert.False(TelemetryNotice.IsSeen(string.Empty, "ada@example.com"));
        Assert.Null(TelemetryNotice.SeenValueFor(null));
        Assert.False(TelemetryNotice.IsSeen(TelemetryNotice.SeenValueFor("ada@example.com"), null));
    }

    [Fact]
    public void Message_PointsToTheSettingsSwitch_AndPromisesNoHealthData()
    {
        Assert.Contains("Send session telemetry", TelemetryNotice.Message);
        Assert.Contains("Settings › Privacy", TelemetryNotice.Message);
        Assert.Contains("never", TelemetryNotice.Message);
        Assert.Contains("health data", TelemetryNotice.Message);
    }
}
