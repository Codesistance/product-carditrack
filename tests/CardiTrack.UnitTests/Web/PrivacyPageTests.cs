using Bunit;
using CardiTrack.Web.Components.Pages;

namespace CardiTrack.UnitTests.Web;

/// <summary>
/// Pins the Art. 13–15 “how alerting works” copy: shipped thresholds, no invented
/// sensitivity slider, model interprets numbers it does not compute.
/// </summary>
public class PrivacyPageTests : BunitContext
{
    [Fact]
    public void PrivacyPage_ExplainsHowAlertingWorks_WithoutClaimingTunableSensitivity()
    {
        var cut = Render<Privacy>();
        var text = cut.Markup;

        Assert.Contains("How alerting works", text);
        Assert.Contains("30%", text);
        Assert.Contains("two standard deviations", text);
        Assert.Contains("5 beats per minute", text);
        Assert.Contains("provisional", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("never computes the numbers", text);
        Assert.Contains("There is no low, medium, or high sensitivity setting yet", text);
    }
}
