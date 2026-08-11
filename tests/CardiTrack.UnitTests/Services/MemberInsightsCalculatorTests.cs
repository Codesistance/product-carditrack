using CardiTrack.Application.Services;

namespace CardiTrack.UnitTests.Services;

public class MemberInsightsCalculatorTests
{
    private static readonly DateTime Now = new(2026, 8, 11, 18, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void No_sync_ever_is_red()
    {
        var (tier, message) = MemberInsightsCalculator.ComputeDataFreshness(
            lastSyncedAt: null, lastAssessedAt: null, now: Now, firstName: "Dad");

        Assert.Equal("red", tier);
        Assert.Contains("Dad", message);
    }

    [Fact]
    public void Sync_over_12_hours_ago_is_red()
    {
        var (tier, _) = MemberInsightsCalculator.ComputeDataFreshness(
            lastSyncedAt: Now.AddHours(-13), lastAssessedAt: null, now: Now, firstName: "Dad");

        Assert.Equal("red", tier);
    }

    [Fact]
    public void Sync_between_4_and_12_hours_ago_is_amber()
    {
        var (tier, _) = MemberInsightsCalculator.ComputeDataFreshness(
            lastSyncedAt: Now.AddHours(-5), lastAssessedAt: null, now: Now, firstName: "Dad");

        Assert.Equal("amber", tier);
    }

    [Fact]
    public void Recent_sync_with_no_assessment_yet_is_blue()
    {
        var (tier, message) = MemberInsightsCalculator.ComputeDataFreshness(
            lastSyncedAt: Now.AddMinutes(-30), lastAssessedAt: null, now: Now, firstName: "Dad");

        Assert.Equal("blue", tier);
        Assert.Equal("Data updated", message);
    }

    [Fact]
    public void Recent_sync_with_an_older_assessment_is_still_blue()
    {
        // The assessment predates this sync, so it doesn't cover the latest data.
        var (tier, _) = MemberInsightsCalculator.ComputeDataFreshness(
            lastSyncedAt: Now.AddMinutes(-30), lastAssessedAt: Now.AddHours(-2),
            now: Now, firstName: "Dad");

        Assert.Equal("blue", tier);
    }

    [Fact]
    public void Recent_sync_with_a_covering_assessment_is_green()
    {
        var (tier, message) = MemberInsightsCalculator.ComputeDataFreshness(
            lastSyncedAt: Now.AddMinutes(-30), lastAssessedAt: Now.AddMinutes(-5),
            now: Now, firstName: "Dad");

        Assert.Equal("green", tier);
        Assert.Equal("Data processed", message);
    }
}
