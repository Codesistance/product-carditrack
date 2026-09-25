using CardiTrack.Mobile.Core.Alerts;

namespace CardiTrack.UnitTests.Mobile;

public class AlertSeverityLookTests
{
    [Theory]
    [InlineData("red", "CRITICAL", "StatusRed")]
    [InlineData("orange", "URGENT", "StatusOrange")]
    [InlineData("yellow", "NOTICE", "StatusYellow")]
    [InlineData("green", "INFO", "StatusGreen")]
    [InlineData(null, "INFO", "StatusUnknown")]
    [InlineData("purple", "INFO", "StatusUnknown")]
    public void EachSeverityHasOneWordAndOneColour(string? severity, string word, string colorKey)
    {
        Assert.Equal((word, colorKey), AlertSeverityLook.For(severity));
    }
}
