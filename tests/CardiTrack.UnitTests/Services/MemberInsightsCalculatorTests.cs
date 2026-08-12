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

    /// <param name="ageYears">
    /// 72 by default — the CardiMembers this product is built for are older adults, and one of the
    /// published reference ranges is split at 65.
    /// </param>
    private static DashboardMetrics Build(ActivityLog log, PatternBaseline? baseline = null, int ageYears = 72) =>
        MemberInsightsCalculator.BuildMetrics([log], baseline, Today, ageYears);

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
        // 6 hours against a 7.5-hour normal — 20% short. That is still inside the 30% green
        // threshold, so it costs stars without costing the card its colour: three of five.
        var metrics = Build(
            new ActivityLog { Date = Yesterday, SleepMinutes = 360 },
            new PatternBaseline { AvgSleepMinutes = 450 });

        Assert.Equal(3, metrics.Sleep.QualityScore);
    }

    [Fact]
    public void A_short_night_slept_efficiently_is_rated_on_its_length_not_its_efficiency()
    {
        // The reading this rating exists to catch. 4.5 hours in bed, all but ten minutes of it
        // asleep: 96% efficiency, which on its own is five stars for a night nowhere near long
        // enough. Efficiency is a ratio and knows nothing about the length of what it divides.
        var metrics = Build(
            new ActivityLog { Date = Yesterday, SleepMinutes = 270, SleepEfficiency = 96 },
            new PatternBaseline { AvgSleepMinutes = 450 });

        Assert.Equal(2, metrics.Sleep.QualityScore);
    }

    [Fact]
    public void A_habitually_short_sleeper_is_still_not_told_their_short_nights_are_five_stars()
    {
        // The same 4.5 hours, but for a member whose own normal is 4.5 hours — so every
        // member-relative comparison the card makes says this night was perfect. It is the one
        // metric where the member's own normal cannot be the whole of the rating: the baseline
        // has learned the very thing a caregiver is watching for.
        var metrics = Build(
            new ActivityLog { Date = Yesterday, SleepMinutes = 270, SleepEfficiency = 96 },
            new PatternBaseline { AvgSleepMinutes = 270 });

        Assert.Equal(2, metrics.Sleep.QualityScore);
        // And the pill still reads the member against themselves, which is why the two are allowed
        // to differ here: this night was normal *for them*, and it was still not enough sleep.
        Assert.Equal("green", metrics.Sleep.Status);
    }

    [Fact]
    public void A_long_enough_night_spent_awake_in_bed_is_rated_on_its_efficiency()
    {
        // The other half of the pair: 7.5 hours in bed, a third of it awake. Long enough by both
        // the member's own normal and the recommendation, and still not a good night.
        var metrics = Build(
            new ActivityLog { Date = Yesterday, SleepMinutes = 450, SleepEfficiency = 65 },
            new PatternBaseline { AvgSleepMinutes = 450 });

        Assert.Equal(2, metrics.Sleep.QualityScore);
    }

    [Fact]
    public void A_night_short_of_the_members_own_normal_is_marked_down_even_when_it_clears_the_recommendation()
    {
        // 7 hours at 95% efficiency — top marks on both the efficiency bands and the published
        // floor. But this member normally sleeps 9, so the night is 22% short of their own normal
        // and the card says so. The recommendation is a floor under the rating, never a ceiling
        // over it.
        var metrics = Build(
            new ActivityLog { Date = Yesterday, SleepMinutes = 420, SleepEfficiency = 95 },
            new PatternBaseline { AvgSleepMinutes = 540 });

        Assert.Equal(3, metrics.Sleep.QualityScore);
    }

    [Theory]
    [InlineData(420, 5)]   // 7.0h — at the published floor
    [InlineData(390, 4)]   // 6.5h
    [InlineData(330, 3)]   // 5.5h
    [InlineData(270, 2)]   // 4.5h
    [InlineData(180, 1)]   // 3.0h
    public void The_length_of_the_night_caps_the_rating_an_hour_at_a_time(int sleepMinutes, int expected)
    {
        // Efficiency and the member's own normal both say five, so the cap is the only thing
        // moving here: one star for each hour short of the NSF's 7-hour floor.
        var metrics = Build(
            new ActivityLog { Date = Yesterday, SleepMinutes = sleepMinutes, SleepEfficiency = 95 },
            new PatternBaseline { AvgSleepMinutes = sleepMinutes });

        Assert.Equal(expected, metrics.Sleep.QualityScore);
    }

    [Fact]
    public void A_night_far_longer_than_the_recommendation_is_marked_down_too()
    {
        // Twelve hours, slept almost end to end, for a member who normally sleeps seven. Every
        // member-relative comparison says five: efficiency is a ratio and does not care how long
        // the night was, and the duration comparison counts only shortfalls, so an overshoot of
        // any size reads as top marks. Only the published band can see this one.
        var metrics = Build(
            new ActivityLog { Date = Yesterday, SleepMinutes = 720, SleepEfficiency = 95 },
            new PatternBaseline { AvgSleepMinutes = 420 });

        Assert.Equal(1, metrics.Sleep.QualityScore);
    }

    [Fact]
    public void Catching_up_on_sleep_is_not_marked_down_for_being_more_than_usual()
    {
        // The asymmetry the cap is careful to preserve. Eight hours against a six-hour normal is a
        // third more sleep than usual, and inside the recommended band for this member's age — a
        // member catching up after a bad week has not earned a worse rating for it.
        var metrics = Build(
            new ActivityLog { Date = Yesterday, SleepMinutes = 480, SleepEfficiency = 95 },
            new PatternBaseline { AvgSleepMinutes = 360 });

        Assert.Equal(5, metrics.Sleep.QualityScore);
    }

    [Fact]
    public void The_ceiling_the_night_is_capped_against_moves_with_the_members_age()
    {
        // Nine hours is the top of the NSF's adult band and an hour past the older-adult one, so
        // the same night rates differently either side of 65 — the one age split among the
        // published ranges, applied to the rating as well as to the band drawn behind the chart.
        var log = new ActivityLog { Date = Yesterday, SleepMinutes = 540, SleepEfficiency = 95 };
        var baseline = new PatternBaseline { AvgSleepMinutes = 540 };

        Assert.Equal(5, Build(log, baseline, ageYears: 64).Sleep.QualityScore);
        Assert.Equal(4, Build(log, baseline, ageYears: 65).Sleep.QualityScore);
    }

    [Fact]
    public void The_cap_reads_the_night_as_measured_not_as_the_card_rounds_it()
    {
        // 418 minutes is 6 hours 58, which the card shows as "7 hours" because Value carries one
        // decimal place. Rounding is the right resolution to read a night at and the wrong one to
        // threshold it on — reading the cap off Value would clear a floor this night is short of.
        var metrics = Build(
            new ActivityLog { Date = Yesterday, SleepMinutes = 418, SleepEfficiency = 95 },
            new PatternBaseline { AvgSleepMinutes = 418 });

        Assert.Equal(7m, metrics.Sleep.Value);
        Assert.Equal(4, metrics.Sleep.QualityScore);
    }

    [Fact]
    public void The_recommendation_lowers_a_sleep_rating_but_never_creates_one()
    {
        // No efficiency and no baseline: nothing of this member's own to rate the night against,
        // so it stays unrated rather than being scored on the population recommendation alone.
        // Rating a lone number against a published range is exactly the reading the product does
        // not make — the cap only ever holds down a rating the member's own data already earned.
        var metrics = Build(new ActivityLog { Date = Yesterday, SleepMinutes = 270 });

        Assert.Null(metrics.Sleep.QualityScore);
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

    // ── Reference ranges (DashboardMetric.Reference) ────────────────────────────
    //
    // The population normal a trend chart draws behind the series, beside this member's own
    // baseline. Published ranges only, each attributed to whoever publishes it.

    [Fact]
    public void Metrics_with_a_published_range_carry_it_with_its_source()
    {
        var metrics = Build(new ActivityLog
        {
            Date = Yesterday,
            RestingHeartRate = 68,
            SleepMinutes = 450,
            SpO2Average = 96m,
            BreathingRate = 14.2m,
        });

        Assert.Equal((60m, 100m, "AHA"), Range(metrics.RestingHeartRate));
        Assert.Equal((7m, 8m, "NSF"), Range(metrics.Sleep));
        Assert.Equal((94m, 100m, "WHO"), Range(metrics.SpO2));
        Assert.Equal((12m, 20m, "WHO"), Range(metrics.BreathingRate));
    }

    [Fact]
    public void The_sleep_range_takes_the_older_adult_band_from_65()
    {
        var log = new ActivityLog { Date = Yesterday, SleepMinutes = 450 };

        // The NSF splits its recommendation at 65: 7–9 hours for adults, 7–8 for older adults.
        // Most CardiMembers are the wrong side of that line, so drawing them the younger band
        // would give them an hour of headroom the recommendation does not.
        Assert.Equal(9m, Build(log, ageYears: 64).Sleep.Reference!.High);
        Assert.Equal(8m, Build(log, ageYears: 65).Sleep.Reference!.High);
        Assert.Equal(8m, Build(log, ageYears: 80).Sleep.Reference!.High);

        // The floor is the same either side of it.
        Assert.Equal(7m, Build(log, ageYears: 64).Sleep.Reference!.Low);
        Assert.Equal(7m, Build(log, ageYears: 80).Sleep.Reference!.Low);
    }

    [Fact]
    public void The_other_published_ranges_are_the_same_at_any_adult_age()
    {
        // A CardiMember is validated as 18-120, so these three are published as one adult band
        // each — narrowing them per member would be our own tailoring under the publisher's name.
        var log = new ActivityLog
        {
            Date = Yesterday,
            RestingHeartRate = 68,
            SpO2Average = 96m,
            BreathingRate = 14.2m,
        };
        var young = Build(log, ageYears: 18);
        var old = Build(log, ageYears: 96);

        Assert.Equal(Range(young.RestingHeartRate), Range(old.RestingHeartRate));
        Assert.Equal(Range(young.SpO2), Range(old.SpO2));
        Assert.Equal(Range(young.BreathingRate), Range(old.BreathingRate));
    }

    [Fact]
    public void A_reference_range_is_carried_even_where_there_is_no_baseline_to_compare_with()
    {
        // SpO2 and breathing rate have no learned baseline, so the published range is the only
        // comparison their charts can draw — which is exactly why it is worth carrying.
        var metrics = Build(new ActivityLog { Date = Yesterday, SpO2Average = 96m, BreathingRate = 14.2m });

        Assert.Null(metrics.SpO2.Baseline);
        Assert.NotNull(metrics.SpO2.Reference);
        Assert.Null(metrics.BreathingRate.Baseline);
        Assert.NotNull(metrics.BreathingRate.Reference);
    }

    [Fact]
    public void Metrics_with_no_published_range_get_none_invented_for_them()
    {
        // No standards body publishes a daily step count, and skin temperature is a
        // wearer-relative measurement with no population normal at all.
        var metrics = Build(new ActivityLog
        {
            Date = Yesterday,
            Steps = 5000,
            Temperature = 36.2m,
            TemperatureBaseline = 36.0m,
        });

        Assert.Null(metrics.Steps.Reference);
        Assert.Null(metrics.Temperature.Reference);
    }

    [Fact]
    public void A_reading_outside_the_published_range_is_still_judged_against_the_member()
    {
        // 96 bpm is inside 60–100 and 40% above this member's own normal. The status follows the
        // member, not the population: the range is drawn as context, never read as a verdict.
        var metrics = Build(
            new ActivityLog { Date = Yesterday, RestingHeartRate = 96 },
            new PatternBaseline { AvgRestingHeartRate = 68 });

        Assert.Equal("yellow", metrics.RestingHeartRate.Status);
        Assert.Equal(60m, metrics.RestingHeartRate.Reference!.Low);
    }

    private static (decimal Low, decimal High, string Source) Range(DashboardMetric metric) =>
        (metric.Reference!.Low, metric.Reference.High, metric.Reference.Source);
}
