using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;

namespace CardiTrack.UnitTests.Services;

public class MemberInsightsCalculatorTests
{
    private static readonly DateTime Now = new(2026, 8, 11, 18, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Today = new(2026, 8, 11);
    private static readonly DateOnly Yesterday = Today.AddDays(-1);

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

    // ── Star ratings (DashboardMetric.QualityScore) ─────────────────────────────
    //
    // Every Key Metrics card that has something to compare against rates its reading out of five,
    // off the same evidence its status colour comes from. The bands nest inside the status
    // thresholds, so a card can never show a green pill next to a one-star rating.

    private static DashboardMetrics Build(ActivityLog log, PatternBaseline? baseline = null) =>
        MemberInsightsCalculator.BuildMetrics([log], baseline, Today);

    [Fact]
    public void A_reading_on_the_members_own_normal_earns_five_stars()
    {
        var metrics = Build(
            new ActivityLog { Date = Yesterday, Steps = 5000, RestingHeartRate = 70 },
            new PatternBaseline { AvgSteps = 5000, AvgRestingHeartRate = 70 });

        Assert.Equal(5, metrics.Steps.QualityScore);
        Assert.Equal(5, metrics.RestingHeartRate.QualityScore);
    }

    [Fact]
    public void Beating_the_normal_on_steps_is_not_marked_down()
    {
        // 60% above the usual day. The status colour treats that as a deviation either way, but
        // the rating reads direction: a member who walked further has not earned fewer stars.
        var metrics = Build(
            new ActivityLog { Date = Yesterday, Steps = 8000 },
            new PatternBaseline { AvgSteps = 5000 });

        Assert.Equal(5, metrics.Steps.QualityScore);
    }

    [Fact]
    public void Falling_well_short_of_the_normal_costs_stars()
    {
        var metrics = Build(
            new ActivityLog { Date = Yesterday, Steps = 2000 },
            new PatternBaseline { AvgSteps = 5000 });

        // -60%: outside the orange threshold, so one star and an orange status agree.
        Assert.Equal(1, metrics.Steps.QualityScore);
        Assert.Equal("orange", metrics.Steps.Status);
    }

    [Fact]
    public void A_resting_heart_rate_is_rated_in_both_directions()
    {
        var low = Build(
            new ActivityLog { Date = Yesterday, RestingHeartRate = 56 },
            new PatternBaseline { AvgRestingHeartRate = 70 });
        var high = Build(
            new ActivityLog { Date = Yesterday, RestingHeartRate = 84 },
            new PatternBaseline { AvgRestingHeartRate = 70 });

        Assert.Equal(3, low.RestingHeartRate.QualityScore);
        Assert.Equal(3, high.RestingHeartRate.QualityScore);
    }

    [Fact]
    public void Steps_for_a_day_still_in_progress_are_not_rated()
    {
        // Same reason ChangePercent stays null: at breakfast every member alive is short of
        // their whole-day average, and a one-star card would be reporting the hour, not the day.
        var metrics = Build(
            new ActivityLog { Date = Today, Steps = 900 },
            new PatternBaseline { AvgSteps = 5000 });

        Assert.Null(metrics.Steps.QualityScore);
    }

    [Fact]
    public void Sleep_is_rated_on_efficiency_when_the_device_reports_it()
    {
        var metrics = Build(
            new ActivityLog { Date = Yesterday, SleepMinutes = 432, SleepEfficiency = 91 },
            new PatternBaseline { AvgSleepMinutes = 450 });

        Assert.Equal(5, metrics.Sleep.QualityScore);
    }

    [Fact]
    public void Sleep_without_an_efficiency_falls_back_to_the_length_of_the_night()
    {
        // 6 hours against a 7.5-hour normal — 20% short, inside the yellow band, so three stars.
        var metrics = Build(
            new ActivityLog { Date = Yesterday, SleepMinutes = 360 },
            new PatternBaseline { AvgSleepMinutes = 450 });

        Assert.Equal(3, metrics.Sleep.QualityScore);
    }

    [Fact]
    public void Skin_temperature_is_rated_against_the_devices_own_nightly_variation()
    {
        var settled = Build(new ActivityLog
        {
            Date = Yesterday,
            Temperature = 36.2m,
            TemperatureBaseline = 36.0m,
            TemperatureVariation = 0.5m,
        });
        var elevated = Build(new ActivityLog
        {
            Date = Yesterday,
            Temperature = 36.9m,
            TemperatureBaseline = 36.0m,
            TemperatureVariation = 0.5m,
        });

        // 0.4 and 1.8 standard deviations out. The pill and the stars agree on both.
        Assert.Equal(5, settled.Temperature.QualityScore);
        Assert.Equal("green", settled.Temperature.Status);
        Assert.Equal(2, elevated.Temperature.QualityScore);
        Assert.Equal("yellow", elevated.Temperature.Status);
    }

    [Fact]
    public void Metrics_with_nothing_to_compare_against_are_left_unrated()
    {
        // No baseline concept exists for either of these yet, and inventing a normal to rate them
        // against is exactly the implied clinical reading the product must not make.
        var metrics = Build(new ActivityLog
        {
            Date = Yesterday,
            Steps = 5000,
            SpO2Average = 96m,
            BreathingRate = 14.2m,
        });

        Assert.Null(metrics.SpO2.QualityScore);
        Assert.Null(metrics.BreathingRate.QualityScore);
        // And no baseline at all leaves the rated metrics unrated too, rather than defaulting.
        Assert.Null(metrics.Steps.QualityScore);
    }
}
