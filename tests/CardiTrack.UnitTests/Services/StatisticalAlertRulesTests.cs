using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// Pins every rule to its boundary. The thresholds are the product's hard-coded "medium"
/// sensitivity, and the null-vs-zero discipline is load-bearing: a day the device did not
/// measure must never read as a day the member did nothing.
/// </summary>
public class StatisticalAlertRulesTests
{
    private static PatternBaseline Baseline() => new()
    {
        CardiMemberId = Guid.NewGuid(),
        PeriodDays = 30,
        AvgSteps = 6000,
        StdDevSteps = 900,
        AvgRestingHeartRate = 62,
        StdDevHeartRate = 2.0m,
        AvgSleepMinutes = 420,
        TypicalWakeTime = new TimeOnly(7, 30),
    };

    private static ActivityLog Log(int? steps = null, int? restingHr = null, int? sleepMinutes = null) => new()
    {
        Date = new DateOnly(2026, 8, 9),
        Steps = steps,
        RestingHeartRate = restingHr,
        SleepMinutes = sleepMinutes,
    };

    // ── activity_decline ─────────────────────────────────────────────────────────────────

    [Fact]
    public void ActivityDecline_Fires_JustBelowSeventyPercentOfBaseline()
    {
        var candidate = StatisticalAlertRules.ActivityDecline(Baseline(), Log(steps: 4199));

        Assert.NotNull(candidate);
        Assert.Equal(AlertType.Inactivity, candidate.Type);
        Assert.Equal(AlertSeverity.Yellow, candidate.Severity);
        Assert.Contains("\"rule\":\"activity_decline\"", candidate.MetricValues);
        Assert.Contains("\"day\":\"2026-08-09\"", candidate.MetricValues);
    }

    // 70% of 6000 is 4200 — the boundary itself is not a decline.
    [Theory]
    [InlineData(4200)]
    [InlineData(6000)]
    public void ActivityDecline_StaysQuiet_AtOrAboveTheBoundary(int steps)
    {
        Assert.Null(StatisticalAlertRules.ActivityDecline(Baseline(), Log(steps: steps)));
    }

    [Fact]
    public void ActivityDecline_TreatsAnUnmeasuredDayAsNothing()
    {
        Assert.Null(StatisticalAlertRules.ActivityDecline(Baseline(), Log(steps: null)));
        Assert.Null(StatisticalAlertRules.ActivityDecline(Baseline(), yesterday: null));
    }

    [Fact]
    public void ActivityDecline_NeedsAStepsBaseline()
    {
        var baseline = Baseline();
        baseline.AvgSteps = null;

        Assert.Null(StatisticalAlertRules.ActivityDecline(baseline, Log(steps: 100)));
    }

    // ── irregular_sleep ──────────────────────────────────────────────────────────────────

    /// <summary>An adult under the NSF's older-adult split, so the recommended band is 7–9 hours.</summary>
    private const int AdultAge = 60;

    /// <summary>Past <see cref="HealthReferenceRanges.OlderAdultAge"/>, so the band is 7–8.</summary>
    private const int OlderAdultAge = 70;

    /// <summary>A baseline whose usual night is far short of the recommendation — the case the
    /// severity split exists for. 30% of 228 min (3.8 h) is 68.4.</summary>
    private static PatternBaseline ShortSleeperBaseline()
    {
        var baseline = Baseline();
        baseline.AvgSleepMinutes = 228;
        return baseline;
    }

    // 30% of 420 min is 126 — both directions past it are irregular.
    [Theory]
    [InlineData(293, "less")]
    [InlineData(547, "more")]
    public void IrregularSleep_Fires_InEitherDirection(int sleep, string direction)
    {
        var candidate = StatisticalAlertRules.IrregularSleep(Baseline(), Log(sleepMinutes: sleep), AdultAge);

        Assert.NotNull(candidate);
        Assert.Equal(AlertType.Sleep, candidate.Type);
        Assert.Contains(direction, candidate.Message);
    }

    /// <summary>
    /// The screenshot case, and the reason the benign branch was retired: someone who normally
    /// manages 3.8 hours slept 5.2. That is 37% off their own usual, but it is movement toward
    /// the recommendation rather than away from it, and the night is over by the time anyone
    /// reads about it. It raises nothing — the fact belongs in the daybook entry, which describes
    /// the finished day, not on a screen whose job is to say what needs attention now.
    /// </summary>
    [Fact]
    public void IrregularSleep_RaisesNothing_WhenALongerNightIsStillShortOfTheRecommendedFloor()
    {
        var candidate = StatisticalAlertRules.IrregularSleep(
            ShortSleeperBaseline(), Log(sleepMinutes: 312), AdultAge);

        Assert.Null(candidate);
    }

    /// <summary>A longer night that reaches the recommended band is the best reading this rule
    /// could produce, and so is not a reading worth paging anyone about — whichever side of the
    /// age split the member is on. Only the ceiling moves, and this lands under both.</summary>
    [Theory]
    [InlineData(AdultAge)]
    [InlineData(OlderAdultAge)]
    public void IrregularSleep_RaisesNothing_WhenALongerNightLandsInsideTheRecommendedBand(int age)
    {
        var candidate = StatisticalAlertRules.IrregularSleep(
            ShortSleeperBaseline(), Log(sleepMinutes: 450), age);

        Assert.Null(candidate);
    }

    /// <summary>
    /// The one direction in which more sleep is still worth flagging — and the one place the
    /// member's age changes the verdict rather than only the wording: 8.5 hours sits inside the
    /// adult band, where it is now silent, and past the older-adult ceiling, where it warns.
    /// The age split is therefore what decides whether anything is raised at all, which is why
    /// <c>ageYears</c> has no default.
    /// </summary>
    [Theory]
    [InlineData(AdultAge, null)]
    [InlineData(OlderAdultAge, AlertSeverity.Yellow)]
    public void IrregularSleep_WarnsOnlyOnceALongerNightOvershootsTheBandForTheirAge(
        int age, AlertSeverity? expected)
    {
        var baseline = Baseline();
        baseline.AvgSleepMinutes = 360;

        var candidate = StatisticalAlertRules.IrregularSleep(baseline, Log(sleepMinutes: 510), age);

        Assert.Equal(expected, candidate?.Severity);
    }

    /// <summary>
    /// The asymmetry is deliberate. A shorter night keeps its warning even when the absolute
    /// figure is perfectly healthy, because losing a third of someone's sleep overnight is a
    /// pattern break in its own right — 8 hours against a 12-hour usual is still a warning.
    /// </summary>
    [Fact]
    public void IrregularSleep_WarnsOnAShorterNight_EvenOneInsideTheRecommendedBand()
    {
        var baseline = Baseline();
        baseline.AvgSleepMinutes = 720;

        var candidate = StatisticalAlertRules.IrregularSleep(baseline, Log(sleepMinutes: 480), AdultAge);

        Assert.NotNull(candidate);
        Assert.Equal(AlertSeverity.Yellow, candidate.Severity);
        Assert.Contains("less", candidate.Message);
    }

    /// <summary>
    /// The sentence quotes hours the way every surface around it does. It used to use F1 while the
    /// comparison card, the chart key and the recommended band beside it all used 0.#, so a
    /// six-hour night read "the usual 6.0" in the message and "6 hours" in the card directly under
    /// it — one figure, two spellings, on one screen.
    /// </summary>
    [Fact]
    public void IrregularSleep_QuotesWholeHoursWithoutATrailingZero()
    {
        var baseline = Baseline();
        baseline.AvgSleepMinutes = 360;

        // Older adult: 8.5 hours is inside the adult band, where a longer night now raises
        // nothing at all, and past the older-adult ceiling, where it still warns. The subject
        // here is how the figures are spelled, so it has to be a night that still produces a
        // message to spell them in.
        var candidate = StatisticalAlertRules.IrregularSleep(baseline, Log(sleepMinutes: 510), OlderAdultAge);

        Assert.NotNull(candidate);
        Assert.Contains("the usual 6 ", candidate.Message);
        Assert.DoesNotContain("6.0", candidate.Message);
        Assert.Contains("8.5 hours of sleep", candidate.Message);
    }

    /// <summary>
    /// The band comparison thresholds on exact minutes, never on the rounded figure the message
    /// prints — <c>MemberInsightsCalculator</c> documents the trap this avoids. Both nights here
    /// print as "8" against an older adult's 8-hour ceiling; only one of them is actually past it.
    /// 481 minutes is 8.02 hours and overshoots, 479 is 7.98 and does not, so a rule reading the
    /// printed figure would grade the pair identically and be wrong about one of them.
    /// </summary>
    /// <remarks>
    /// The floor no longer appears here because nothing thresholds on it any more: a longer night
    /// short of the recommendation was the benign shape, and retiring it took the only comparison
    /// against <c>recommended.Low</c> with it. The ceiling is now the rule's one band boundary,
    /// which makes it the one place this trap can still bite.
    /// </remarks>
    [Theory]
    [InlineData(481, AlertSeverity.Yellow)]
    [InlineData(479, null)]
    public void IrregularSleep_ThresholdsOnExactMinutes_NotTheRoundedFigureItPrints(
        int sleepMinutes, AlertSeverity? expected)
    {
        var baseline = Baseline();
        baseline.AvgSleepMinutes = 240;

        var candidate = StatisticalAlertRules.IrregularSleep(
            baseline, Log(sleepMinutes: sleepMinutes), OlderAdultAge);

        Assert.Equal(expected, candidate?.Severity);
        if (candidate is not null)
        {
            Assert.Contains("Around 8 hours", candidate.Message);
            Assert.Contains("past the 8 hours recommended", candidate.Message);
        }
    }

    /// <summary>
    /// The band the night was judged against is written down, not left to be re-derived when the
    /// detail screen draws it — a member who crosses the older-adult split later must not get an
    /// alert quoting one ceiling beside a chart shading another.
    /// </summary>
    [Fact]
    public void IrregularSleep_RecordsTheBandItJudgedAgainst()
    {
        var adult = StatisticalAlertRules.IrregularSleep(Baseline(), Log(sleepMinutes: 293), AdultAge);
        var older = StatisticalAlertRules.IrregularSleep(Baseline(), Log(sleepMinutes: 293), OlderAdultAge);

        Assert.NotNull(adult);
        Assert.NotNull(older);
        Assert.Contains("\"recommendedLowHours\":7", adult.MetricValues);
        Assert.Contains("\"recommendedHighHours\":9", adult.MetricValues);
        Assert.Contains("\"recommendedHighHours\":8", older.MetricValues);
    }

    /// <summary>
    /// The candidate names the night it judged — the log's own date, the civil day the night
    /// ended on — both on the record for the orchestrator's per-night dedup and in the stored
    /// MetricValues, so the same night can never alert twice however late its data arrived.
    /// </summary>
    [Fact]
    public void IrregularSleep_NamesTheNightItJudged()
    {
        var candidate = StatisticalAlertRules.IrregularSleep(Baseline(), Log(sleepMinutes: 200), AdultAge);

        Assert.NotNull(candidate);
        Assert.Equal(new DateOnly(2026, 8, 9), candidate.NightOf);
        Assert.Contains("\"night\":\"2026-08-09\"", candidate.MetricValues);
    }

    [Theory]
    [InlineData(294)]
    [InlineData(420)]
    [InlineData(546)]
    public void IrregularSleep_StaysQuiet_WithinTheBand(int sleep)
    {
        Assert.Null(StatisticalAlertRules.IrregularSleep(Baseline(), Log(sleepMinutes: sleep), AdultAge));
        Assert.False(StatisticalAlertRules.SleepDepartsFromBaseline(Baseline(), Log(sleepMinutes: sleep)));
    }

    /// <summary>
    /// The trigger the digest asks on its own. It has to stay symmetric — the grading is what
    /// changed, not what counts as a departure — or a longer night would stop refreshing a
    /// summary that is about to describe it.
    /// </summary>
    [Theory]
    [InlineData(293)]
    [InlineData(547)]
    public void SleepDepartsFromBaseline_IsSymmetric(int sleep)
    {
        Assert.True(StatisticalAlertRules.SleepDepartsFromBaseline(Baseline(), Log(sleepMinutes: sleep)));
    }

    // ── elevated_heart_rate ──────────────────────────────────────────────────────────────

    // σ = 2 → 2σ = 4 < the 5 bpm floor, so the margin is 5: fires above 67, not at it.
    [Fact]
    public void ElevatedHeartRate_UsesTheFloor_WhenSigmaIsTight()
    {
        Assert.Null(StatisticalAlertRules.ElevatedHeartRate(Baseline(), Log(restingHr: 67)));

        var candidate = StatisticalAlertRules.ElevatedHeartRate(Baseline(), Log(restingHr: 68));
        Assert.NotNull(candidate);
        Assert.Equal(AlertType.HeartRate, candidate.Type);
        Assert.Equal(AlertSeverity.Orange, candidate.Severity);
        Assert.Contains("\"day\":\"2026-08-09\"", candidate.MetricValues);
    }

    // σ = 6 → 2σ = 12 beats the floor: 62 + 12 = 74 is the boundary.
    [Fact]
    public void ElevatedHeartRate_UsesTwoSigma_WhenItIsWider()
    {
        var baseline = Baseline();
        baseline.StdDevHeartRate = 6.0m;

        Assert.Null(StatisticalAlertRules.ElevatedHeartRate(baseline, Log(restingHr: 74)));
        Assert.NotNull(StatisticalAlertRules.ElevatedHeartRate(baseline, Log(restingHr: 75)));
    }

    [Fact]
    public void ElevatedHeartRate_WithNoSigmaRecorded_StillHasTheFloor()
    {
        var baseline = Baseline();
        baseline.StdDevHeartRate = null;

        Assert.Null(StatisticalAlertRules.ElevatedHeartRate(baseline, Log(restingHr: 67)));
        Assert.NotNull(StatisticalAlertRules.ElevatedHeartRate(baseline, Log(restingHr: 68)));
    }

    // ── no_morning_activity ──────────────────────────────────────────────────────────────

    // Wake 07:30 + 2h grace → the rule arms at 09:30 local.
    [Fact]
    public void NoMorningActivity_Fires_OnAMeasuredZero_PastWakePlusGrace()
    {
        var candidate = StatisticalAlertRules.NoMorningActivity(
            Baseline(), Log(steps: 0), new DateTime(2026, 8, 10, 9, 30, 0));

        Assert.NotNull(candidate);
        Assert.Equal(AlertType.PatternBreak, candidate.Type);
        Assert.Equal(AlertSeverity.Red, candidate.Severity);
    }

    [Fact]
    public void NoMorningActivity_StaysQuiet_DuringTheGrace()
    {
        Assert.Null(StatisticalAlertRules.NoMorningActivity(
            Baseline(), Log(steps: 0), new DateTime(2026, 8, 10, 9, 29, 0)));
    }

    // The red severity makes this the rule where null-vs-zero matters most: an HR-only device
    // reports no steps field at all, and that absence must never page a family.
    [Fact]
    public void NoMorningActivity_NeverFires_OnAnUnmeasuredSteps()
    {
        Assert.Null(StatisticalAlertRules.NoMorningActivity(
            Baseline(), Log(steps: null), new DateTime(2026, 8, 10, 12, 0, 0)));
        Assert.Null(StatisticalAlertRules.NoMorningActivity(
            Baseline(), today: null, new DateTime(2026, 8, 10, 12, 0, 0)));
    }

    [Fact]
    public void NoMorningActivity_NeedsATypicalWakeTime()
    {
        var baseline = Baseline();
        baseline.TypicalWakeTime = null;

        Assert.Null(StatisticalAlertRules.NoMorningActivity(
            baseline, Log(steps: 0), new DateTime(2026, 8, 10, 12, 0, 0)));
    }

    // ── long_term_trend ──────────────────────────────────────────────────────────────────

    private static Dictionary<DateOnly, ActivityLog> WeeklySteps(
        DateOnly yesterday, params int[] weeklyAveragesNewestFirst)
    {
        var logs = new Dictionary<DateOnly, ActivityLog>();
        for (var week = 0; week < weeklyAveragesNewestFirst.Length; week++)
        {
            for (var offset = 0; offset < 7; offset++)
            {
                var date = yesterday.AddDays(-7 * week - offset);
                logs[date] = new ActivityLog { Date = date, Steps = weeklyAveragesNewestFirst[week] };
            }
        }

        return logs;
    }

    private static readonly DateOnly Yesterday = new(2026, 8, 9);

    [Fact]
    public void LongTermTrend_Fires_OnFourWeeksOfSustainedDecline()
    {
        // Each week ≥5% below the one before: 7000 → 6600 → 6200 → 5800.
        var logs = WeeklySteps(Yesterday, 5800, 6200, 6600, 7000);

        var candidate = StatisticalAlertRules.LongTermTrend(logs, Yesterday);

        Assert.NotNull(candidate);
        Assert.Equal(AlertType.Trend, candidate.Type);
        Assert.Equal(AlertSeverity.Orange, candidate.Severity);
        Assert.Contains("\"rule\":\"long_term_trend\"", candidate.MetricValues);
        Assert.Contains("\"day\":\"2026-08-09\"", candidate.MetricValues);
    }

    // One recovering week breaks the pattern — a sustained trend, not three bad patches.
    [Fact]
    public void LongTermTrend_StaysQuiet_WhenOneWeekRecovers()
    {
        var logs = WeeklySteps(Yesterday, 5800, 6900, 6600, 7000);

        Assert.Null(StatisticalAlertRules.LongTermTrend(logs, Yesterday));
    }

    // A 4% weekly decline is ordinary drift, not a trend.
    [Fact]
    public void LongTermTrend_StaysQuiet_BelowTheWeeklyThreshold()
    {
        var logs = WeeklySteps(Yesterday, 6367, 6632, 6908, 7196);

        Assert.Null(StatisticalAlertRules.LongTermTrend(logs, Yesterday));
    }

    [Fact]
    public void LongTermTrend_NeedsEnoughMeasuredDays_InEveryWeek()
    {
        var logs = WeeklySteps(Yesterday, 5800, 6200, 6600, 7000);
        // Hollow out the oldest week to 3 measured days.
        for (var offset = 0; offset < 4; offset++)
            logs.Remove(Yesterday.AddDays(-21 - offset));

        Assert.Null(StatisticalAlertRules.LongTermTrend(logs, Yesterday));
    }

    [Fact]
    public void LongTermTrend_IgnoresUnmeasuredDays_RatherThanCountingThemAsZero()
    {
        var logs = WeeklySteps(Yesterday, 5800, 6200, 6600, 7000);
        // Null out one day per week: averages must be unaffected, so it still fires.
        foreach (var week in Enumerable.Range(0, 4))
            logs[Yesterday.AddDays(-7 * week)].Steps = null;

        Assert.NotNull(StatisticalAlertRules.LongTermTrend(logs, Yesterday));
    }

    // ── hrv_drop ─────────────────────────────────────────────────────────────────────────

    private static PatternBaseline HrvBaseline(decimal average = 40m, decimal? stdDev = 2m) =>
        new()
        {
            PeriodDays = 30,
            AvgHeartRateVariabilityMs = average,
            StdDevHeartRateVariability = stdDev,
        };

    private static ActivityLog HrvLog(DateOnly date, decimal? hrv) =>
        new() { Date = date, HeartRateVariabilityMs = hrv };

    /// <summary>
    /// The floor is 15% of their own average, not 2σ, whenever 2σ is the smaller — a member whose
    /// HRV barely moves night to night would otherwise be alerted over a millisecond.
    /// </summary>
    [Fact]
    public void HeartRateVariabilityDrop_Fires_WhenBothNightsAreBelowTheProportionalFloor()
    {
        // 15% of 40 is 6, so the threshold is 34; 2σ is only 4.
        var candidate = StatisticalAlertRules.HeartRateVariabilityDrop(
            HrvBaseline(),
            HrvLog(new DateOnly(2026, 8, 10), 31m),
            HrvLog(new DateOnly(2026, 8, 9), 33m));

        Assert.NotNull(candidate);
        Assert.Equal(AlertType.HeartRate, candidate.Type);
        Assert.Equal(AlertSeverity.Orange, candidate.Severity);
        Assert.Contains("\"rule\":\"hrv_drop\"", candidate.MetricValues);
        Assert.Equal(new DateOnly(2026, 8, 10), candidate.NightOf);
    }

    /// <summary>
    /// Where the member's own variability is wide, 2σ takes over from the floor: 2×8 is 16, so a
    /// night at 30 against a usual 40 is inside their ordinary range and says nothing.
    /// </summary>
    [Fact]
    public void HeartRateVariabilityDrop_UsesTwoSigma_WhenItIsWiderThanTheFloor()
    {
        Assert.Null(StatisticalAlertRules.HeartRateVariabilityDrop(
            HrvBaseline(stdDev: 8m),
            HrvLog(new DateOnly(2026, 8, 10), 30m),
            HrvLog(new DateOnly(2026, 8, 9), 30m)));
    }

    // One low night is a late meal, a glass of wine or a bad night — not a signal.
    [Fact]
    public void HeartRateVariabilityDrop_StaysSilent_OnASingleLowNight()
    {
        Assert.Null(StatisticalAlertRules.HeartRateVariabilityDrop(
            HrvBaseline(),
            HrvLog(new DateOnly(2026, 8, 10), 28m),
            HrvLog(new DateOnly(2026, 8, 9), 41m)));
    }

    // A missing previous night is one night, not two, and never fires — null is not permission.
    [Fact]
    public void HeartRateVariabilityDrop_StaysSilent_WhenThePreviousNightWasNotMeasured()
    {
        Assert.Null(StatisticalAlertRules.HeartRateVariabilityDrop(
            HrvBaseline(),
            HrvLog(new DateOnly(2026, 8, 10), 28m),
            HrvLog(new DateOnly(2026, 8, 9), null)));
    }

    // ── irregular_rhythm and ecg_afib ────────────────────────────────────────────────────

    private static ActivityLog RhythmLog(
        DateOnly date, int? notifications = null, int? ecgReadings = null, int? afib = null) =>
        new()
        {
            Date = date,
            IrregularRhythmNotifications = notifications,
            EcgReadings = ecgReadings,
            EcgAtrialFibrillationReadings = afib,
        };

    [Fact]
    public void IrregularRhythm_Fires_OnTheDeviceOwnNotification()
    {
        var candidate = StatisticalAlertRules.IrregularRhythm(
            RhythmLog(new DateOnly(2026, 8, 10), notifications: 1), yesterday: null);

        Assert.NotNull(candidate);
        Assert.Equal(AlertType.Rhythm, candidate.Type);
        Assert.Equal(AlertSeverity.Orange, candidate.Severity);
        Assert.Contains("\"rule\":\"irregular_rhythm\"", candidate.MetricValues);
    }

    // Zero is the reassuring reading and null is the unreadable one; neither is an alert.
    [Theory]
    [InlineData(0)]
    [InlineData(null)]
    public void IrregularRhythm_StaysSilent_WithoutANotification(int? notifications)
    {
        Assert.Null(StatisticalAlertRules.IrregularRhythm(
            RhythmLog(new DateOnly(2026, 8, 10), notifications), yesterday: null));
    }

    // An event late in the member's evening can be read by a pass that has already rolled over.
    [Fact]
    public void IrregularRhythm_ReadsYesterday_WhenTodayCarriesNoEvent()
    {
        var candidate = StatisticalAlertRules.IrregularRhythm(
            RhythmLog(new DateOnly(2026, 8, 10)),
            RhythmLog(new DateOnly(2026, 8, 9), notifications: 2));

        Assert.NotNull(candidate);
        Assert.Equal(new DateOnly(2026, 8, 9), candidate.NightOf);
    }

    [Fact]
    public void EcgAtrialFibrillation_IsTheOneRedRhythmFinding()
    {
        var candidate = StatisticalAlertRules.EcgAtrialFibrillation(
            RhythmLog(new DateOnly(2026, 8, 10), ecgReadings: 2, afib: 1), yesterday: null);

        Assert.NotNull(candidate);
        Assert.Equal(AlertType.Rhythm, candidate.Type);
        Assert.Equal(AlertSeverity.Red, candidate.Severity);
        Assert.Contains("\"rule\":\"ecg_afib\"", candidate.MetricValues);
    }

    // A reading the device declined to classify is counted as an ECG and never as a finding.
    [Fact]
    public void EcgAtrialFibrillation_StaysSilent_WhenNoReadingWasClassifiedAsAfib()
    {
        Assert.Null(StatisticalAlertRules.EcgAtrialFibrillation(
            RhythmLog(new DateOnly(2026, 8, 10), ecgReadings: 3, afib: 0), yesterday: null));
    }

    // ── rapid_weight_gain ────────────────────────────────────────────────────────────────

    private static Dictionary<DateOnly, ActivityLog> Weights(DateOnly today, params (int Offset, decimal? Kg)[] days) =>
        days.ToDictionary(
            d => today.AddDays(-d.Offset),
            d => new ActivityLog { Date = today.AddDays(-d.Offset), WeightKg = d.Kg });

    [Fact]
    public void RapidWeightGain_Fires_OnAKiloAndAHalfInThreeDays()
    {
        var today = new DateOnly(2026, 8, 10);
        var candidate = StatisticalAlertRules.RapidWeightGain(
            Weights(today, (0, 79.6m), (3, 78.1m)), today);

        Assert.NotNull(candidate);
        Assert.Equal(AlertType.Trend, candidate.Type);
        Assert.Equal(AlertSeverity.Orange, candidate.Severity);
        Assert.Contains("\"rule\":\"rapid_weight_gain\"", candidate.MetricValues);
        Assert.Contains("\"windowDays\":3", candidate.MetricValues);
    }

    // Under the three-day threshold but over the week's: the longer window catches the slower rise.
    [Fact]
    public void RapidWeightGain_Fires_OnTheWeekThresholdWhenTheThreeDayOneHolds()
    {
        var today = new DateOnly(2026, 8, 10);
        var candidate = StatisticalAlertRules.RapidWeightGain(
            Weights(today, (0, 80.5m), (3, 79.9m), (7, 78.1m)), today);

        Assert.NotNull(candidate);
        Assert.Contains("\"windowDays\":7", candidate.MetricValues);
    }

    [Fact]
    public void RapidWeightGain_StaysSilent_BelowBothThresholds()
    {
        var today = new DateOnly(2026, 8, 10);
        Assert.Null(StatisticalAlertRules.RapidWeightGain(
            Weights(today, (0, 78.9m), (3, 78.1m), (7, 77.9m)), today));
    }

    // Two readings days apart are the whole measurement — one weighing is not a change.
    [Fact]
    public void RapidWeightGain_StaysSilent_WithoutAnEarlierWeighing()
    {
        var today = new DateOnly(2026, 8, 10);
        Assert.Null(StatisticalAlertRules.RapidWeightGain(Weights(today, (0, 82m)), today));
    }

    // Weighings are sparse: a member who did not step on the scale today still has yesterday's.
    [Fact]
    public void RapidWeightGain_ReadsYesterdayWeighing_WhenTodayHasNone()
    {
        var today = new DateOnly(2026, 8, 10);
        var candidate = StatisticalAlertRules.RapidWeightGain(
            Weights(today, (1, 79.6m), (4, 78.1m)), today);

        Assert.NotNull(candidate);
        Assert.Equal(today.AddDays(-1), candidate.NightOf);
    }

    // ── blood_sugar_out_of_range ─────────────────────────────────────────────────────────

    private static ActivityLog GlucoseLog(DateOnly date, decimal? min = null, decimal? max = null) =>
        new() { Date = date, BloodGlucoseMin = min, BloodGlucoseMax = max };

    /// <summary>
    /// The low side is red and the high side orange, deliberately: a high reading is an afternoon
    /// to manage, a low one can take someone off their feet within the hour.
    /// </summary>
    [Fact]
    public void BloodSugarOutOfRange_IsRedOnTheLowSide()
    {
        var candidate = StatisticalAlertRules.BloodSugarOutOfRange(
            GlucoseLog(new DateOnly(2026, 8, 10), min: 61m, max: 150m), yesterday: null);

        Assert.NotNull(candidate);
        Assert.Equal(AlertSeverity.Red, candidate.Severity);
        Assert.Contains("\"rule\":\"blood_sugar_out_of_range\"", candidate.MetricValues);
    }

    [Fact]
    public void BloodSugarOutOfRange_IsOrangeOnTheHighSide()
    {
        var candidate = StatisticalAlertRules.BloodSugarOutOfRange(
            GlucoseLog(new DateOnly(2026, 8, 10), min: 110m, max: 265m), yesterday: null);

        Assert.NotNull(candidate);
        Assert.Equal(AlertSeverity.Orange, candidate.Severity);
    }

    // The thresholds sit well outside the 70-180 target band the dashboard draws: a reading inside
    // that band's shoulder is an ordinary day for someone managing their diabetes.
    [Fact]
    public void BloodSugarOutOfRange_StaysSilent_JustOutsideTheTargetBand()
    {
        Assert.Null(StatisticalAlertRules.BloodSugarOutOfRange(
            GlucoseLog(new DateOnly(2026, 8, 10), min: 70m, max: 250m), yesterday: null));
    }

    [Fact]
    public void BloodSugarOutOfRange_StaysSilent_WhenNoReadingsWereTaken()
    {
        Assert.Null(StatisticalAlertRules.BloodSugarOutOfRange(
            GlucoseLog(new DateOnly(2026, 8, 10)), yesterday: null));
    }
}
