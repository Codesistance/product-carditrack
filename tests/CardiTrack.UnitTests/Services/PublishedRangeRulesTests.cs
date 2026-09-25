using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// The published-range alert rules (decision 2026-09-25): sleep, resting heart rate and blood
/// oxygen outside the range published for them on at least three of the last five days with a
/// reading, whatever the member's own usual is — one alert per stretch, with the week-on-week
/// trend stated for the model to weigh. Figures are invented.
/// </summary>
public class PublishedRangeRulesTests
{
    private static readonly DateOnly Latest = new(2026, 9, 25);

    /// <summary>Nights oldest first, ending on <see cref="Latest"/>; null is a night with no reading.</summary>
    private static Dictionary<DateOnly, ActivityLog> Nights(params int?[] minutesOldestFirst) =>
        minutesOldestFirst
            .Select((minutes, i) => new ActivityLog
            {
                Date = Latest.AddDays(i - minutesOldestFirst.Length + 1),
                SleepMinutes = minutes,
                NightStatus = minutes is null ? NightSleepStatus.NoData : NightSleepStatus.Slept,
            })
            .ToDictionary(l => l.Date);

    // ── sleep_outside_range ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Five-hour nights for a member whose usual is five hours: <see cref="StatisticalAlertRules.IrregularSleep"/>
    /// says nothing, because nothing departed from their usual. This rule is the one that does.
    /// </summary>
    [Fact]
    public void SleepBelowTheRange_OnThreeOfFive_Fires_EvenAtTheirUsual()
    {
        var finding = StatisticalAlertRules.SleepOutsideRange(
            Nights(450, 300, 440, 290, 310), Latest, ageYears: 80,
            new PatternBaseline { AvgSleepMinutes = 300 });

        Assert.NotNull(finding);
        Assert.Equal(StatisticalAlertRules.SleepOutsideRangeRule, finding.Rule);
        Assert.Equal(AlertType.Sleep, finding.Type);
        Assert.Contains("outside the 7-8 hours a night recommended at their age (NSF) on 3 of the last 5 nights", finding.Observation);
        Assert.Contains("Their usual is 5 hours, itself outside the range.", finding.Observation);
    }

    [Fact]
    public void TwoNightsOfFive_IsNotAPattern() =>
        Assert.Null(StatisticalAlertRules.SleepOutsideRange(
            Nights(450, 300, 440, 290, 460), Latest, ageYears: 80, baseline: null));

    /// <summary>
    /// A night with no reading neither counts nor breaks the count: three measured nights, all short,
    /// is three of three.
    /// </summary>
    [Fact]
    public void UnmeasuredNights_AreSkipped_NotCountedAsInsideTheRange()
    {
        var finding = StatisticalAlertRules.SleepOutsideRange(
            Nights(300, null, 290, null, 310), Latest, ageYears: 80, baseline: null);

        Assert.NotNull(finding);
        Assert.Contains("on 3 of the last 3 nights with a reading", finding.Observation);
    }

    /// <summary>An awake night — worn all night, no sleep — is a night of no sleep, and is named as one.</summary>
    [Fact]
    public void AnAwakeNight_Counts_AndIsNamed()
    {
        var nights = Nights(450, 460, 300, 290, 0);
        nights[Latest].NightStatus = NightSleepStatus.Awake;

        var finding = StatisticalAlertRules.SleepOutsideRange(nights, Latest, ageYears: 80, baseline: null);

        Assert.NotNull(finding);
        Assert.Contains($"2026-09-25 {ReadingFigures.AwakeNight}", finding.Observation);
    }

    /// <summary>
    /// With no age the seven-hour floor is still judged — it holds at every adult age — and the
    /// ceiling, which moves at 65, is not guessed.
    /// </summary>
    [Fact]
    public void WithNoAge_LongNightsAreNotJudged_ButShortOnesAre()
    {
        Assert.Null(StatisticalAlertRules.SleepOutsideRange(
            Nights(560, 570, 580, 590, 600), Latest, ageYears: null, baseline: null));
        Assert.NotNull(StatisticalAlertRules.SleepOutsideRange(
            Nights(300, 310, 320, 330, 340), Latest, ageYears: null, baseline: null));
    }

    [Fact]
    public void LongNightsPastTheCeiling_AtTheirAge_Fire()
    {
        var finding = StatisticalAlertRules.SleepOutsideRange(
            Nights(560, 570, 580, 590, 600), Latest, ageYears: 80, baseline: null);

        Assert.NotNull(finding);
        Assert.Contains("Most recently above the range", finding.Observation);
    }

    // ── the stretch ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The stretch starts after the last run of three nights back inside the range, and a night or
    /// two inside does not end it — so it is one stretch, keyed once, however long it runs.
    /// </summary>
    [Fact]
    public void TheStretch_RunsBackPastAShortPause_ToTheLastFullReset()
    {
        // Oldest first: three nights inside (the reset), then short, a one-night pause, short again.
        var finding = StatisticalAlertRules.SleepOutsideRange(
            Nights(450, 450, 450, 300, 300, 450, 300, 300, 300), Latest, ageYears: 80, baseline: null);

        Assert.NotNull(finding);
        Assert.Equal(Latest.AddDays(-5), finding.StretchStart);
        Assert.Contains("outside it since 2026-09-20", finding.Observation);
    }

    // ── worsening ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void AWeeklyAverageMovingFurtherOutside_IsStated()
    {
        var nights = Enumerable.Repeat<int?>(390, 7)
            .Concat(Enumerable.Repeat<int?>(360, 7))
            .Concat(Enumerable.Repeat<int?>(330, 7))
            .ToArray();

        var finding = StatisticalAlertRules.SleepOutsideRange(Nights(nights), Latest, ageYears: 80, baseline: null);

        Assert.NotNull(finding);
        Assert.Contains("Weekly averages, oldest first: 6.5 hours, 6 hours, 5.5 hours — further outside the range each week.", finding.Observation);
        Assert.Contains("\"worsening\":true", finding.MetricValues);
    }

    [Fact]
    public void ASteadyStretch_IsStatedAsNotWorsening()
    {
        var finding = StatisticalAlertRules.SleepOutsideRange(
            Nights(Enumerable.Repeat<int?>(330, 21).ToArray()), Latest, ageYears: 80, baseline: null);

        Assert.NotNull(finding);
        Assert.Contains("not moving further outside it week on week.", finding.Observation);
        Assert.Contains("\"worsening\":false", finding.MetricValues);
    }

    // ── resting_hr_outside_range ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(104)]
    [InlineData(54)]
    public void RestingHeartRateOutsideTheRange_EitherSide_Fires(int resting)
    {
        var days = Enumerable.Range(0, 5)
            .Select(i => new ActivityLog { Date = Latest.AddDays(i - 4), RestingHeartRate = i % 2 == 0 ? resting : 72 })
            .ToDictionary(l => l.Date);

        var finding = StatisticalAlertRules.RestingHeartRateOutsideRange(days, Latest, baseline: null);

        Assert.NotNull(finding);
        Assert.Equal(AlertType.HeartRate, finding.Type);
        Assert.Contains("outside the 60-100 bpm published for an adult at rest (AHA) on 3 of the last 5 days", finding.Observation);
    }

    [Fact]
    public void RestingHeartRateInsideTheRange_IsQuiet_EvenFarFromTheirUsual()
    {
        var days = Enumerable.Range(0, 5)
            .Select(i => new ActivityLog { Date = Latest.AddDays(i - 4), RestingHeartRate = 95 })
            .ToDictionary(l => l.Date);

        Assert.Null(StatisticalAlertRules.RestingHeartRateOutsideRange(
            days, Latest, new PatternBaseline { AvgRestingHeartRate = 62 }));
    }

    // ── spo2_below_range ────────────────────────────────────────────────────────────────

    [Fact]
    public void OxygenBelowTheFloor_OnThreeOfFive_Fires()
    {
        var days = Enumerable.Range(0, 5)
            .Select(i => new ActivityLog { Date = Latest.AddDays(i - 4), SpO2Average = i < 3 ? 97m : 92.5m })
            .ToDictionary(l => l.Date);
        days[Latest.AddDays(-4)].SpO2Average = 93m;

        var finding = StatisticalAlertRules.OxygenBelowRange(days, Latest);

        Assert.NotNull(finding);
        Assert.Equal(StatisticalAlertRules.OxygenBelowRangeRule, finding.Rule);
        Assert.Contains("outside the 94-100% published range (WHO) on 3 of the last 5 days", finding.Observation);
        Assert.Contains("2026-09-25 92.5%", finding.Observation);
    }

    [Fact]
    public void TheRangeRules_AreInEveryRuleList()
    {
        foreach (var rule in StatisticalAlertRules.PublishedRangeRules)
        {
            Assert.Contains(rule, StatisticalAlertRules.AllRules);
            Assert.True(AlertRuleCatalogue.IsImplemented(rule), $"{rule} is missing from the settings catalogue.");
        }
    }
}
