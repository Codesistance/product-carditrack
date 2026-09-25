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
        var text = string.Join(
            ' ',
            cut.FindAll("p").Select(p =>
                string.Join(' ', p.TextContent.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))));

        Assert.Contains("How alerting works", cut.Markup);
        Assert.Contains("30%", text);
        Assert.Contains("two standard deviations", text);
        Assert.Contains("5 beats per minute", text);
        Assert.Contains("provisional", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("never computes the numbers", text);
        Assert.Contains("There is no low, medium, or high sensitivity setting yet", text);
    }

    [Fact]
    public void PrivacyPage_DisclosesDefaultOnAppTelemetry_AndHowToTurnItOff()
    {
        var cut = Render<Privacy>();
        var text = string.Join(
            ' ',
            cut.FindAll("p").Select(p =>
                string.Join(' ', p.TextContent.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))));

        Assert.Contains("App diagnostics", cut.Markup);
        Assert.Contains("on by default", text);
        Assert.Contains("never includes health readings", text);
        Assert.Contains("turn this off at any time", text);
        Assert.Contains("Settings → Privacy → Send session telemetry", text);
        Assert.Contains("match it to your account", text);
        Assert.DoesNotContain("not linked to your account", text);
    }

    /// <summary>
    /// The published-range decision (2026-09-25), as a caregiver reads it: for sleep, resting heart
    /// rate and blood oxygen the published range is the normal, and those readings can alert before
    /// a month of history exists. The page used to say CardiTrack compared people only with their
    /// own history, "not a population average", and raised no statistical alert in the first
    /// month — both untrue once the published-range rules shipped.
    /// </summary>
    [Fact]
    public void PrivacyPage_SaysThePublishedRangeIsTheNormal_AndCanAlertInTheFirstWeeks()
    {
        var cut = Render<Privacy>();
        var text = string.Join(
            ' ',
            cut.FindAll("p").Select(p =>
                string.Join(' ', p.TextContent.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))));

        Assert.Contains("that range is what counts as normal", text);
        Assert.Contains("60–100 beats per minute", text);
        Assert.Contains("94%", text);
        Assert.Contains("can raise an alert from the first week", text);
        Assert.Contains("once for each stretch", text);
        Assert.DoesNotContain("not a population average", text);
    }
}
