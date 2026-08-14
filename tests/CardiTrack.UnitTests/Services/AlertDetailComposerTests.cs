using System.Globalization;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;

namespace CardiTrack.UnitTests.Services;

public class AlertDetailComposerTests
{
    private readonly DateOnly _today = new(2026, 8, 14);
    private readonly Guid _memberId = Guid.NewGuid();

    [Fact]
    public void ReadRule_ReturnsTheProducerStamp()
    {
        Assert.Equal(
            StatisticalAlertRules.ActivityDeclineRule,
            AlertDetailComposer.ReadRule("""{"rule":"activity_decline","steps":2500}"""));
        Assert.Null(AlertDetailComposer.ReadRule(null));
        Assert.Null(AlertDetailComposer.ReadRule("not-json"));
    }

    [Theory]
    [InlineData(StatisticalAlertRules.ActivityDeclineRule, 14)]
    [InlineData(StatisticalAlertRules.NoMorningActivityRule, 14)]
    [InlineData(StatisticalAlertRules.LongTermTrendRule, 28)]
    [InlineData(StatisticalAlertRules.ElevatedHeartRateRule, 7)]
    [InlineData(StatisticalAlertRules.IrregularSleepRule, 14)]
    [InlineData(AlertDetailComposer.DeviceSilenceRule, 0)]
    [InlineData(AlertDetailComposer.RealtimeHeartRateRule, 0)]
    [InlineData(null, 0)]
    public void DailyLogDays_IsTheWindowForThatRuleOnly(string? rule, int days) =>
        Assert.Equal(days, AlertDetailComposer.DailyLogDays(rule));

    [Fact]
    public void NeedsGranular_OnlyForTheRealtimeHeartRule()
    {
        Assert.True(AlertDetailComposer.NeedsGranular(AlertDetailComposer.RealtimeHeartRateRule));
        Assert.False(AlertDetailComposer.NeedsGranular(StatisticalAlertRules.ActivityDeclineRule));
        Assert.False(AlertDetailComposer.NeedsGranular(StatisticalAlertRules.ElevatedHeartRateRule));
    }

    [Fact]
    public void ActivityDecline_PlotsStepsNotHeartRate()
    {
        var alert = MakeAlert(
            AlertType.Inactivity,
            """{"rule":"activity_decline","steps":2500,"baselineAvgSteps":5000}""");
        var logs = new[]
        {
            Log(_today.AddDays(-1), steps: 2500, restingHr: 88, sleepMinutes: 400),
            Log(_today.AddDays(-2), steps: 4800, restingHr: 70, sleepMinutes: 450),
        };

        var detail = AlertDetailComposer.Compose(alert, Member(), null, logs, _today, null, null);

        Assert.Equal("activity_decline", detail.Rule);
        Assert.NotNull(detail.Chart);
        Assert.Equal("steps", detail.Chart!.Metric);
        Assert.Equal(14, detail.Chart.Series.Count);
        Assert.Equal(2500, detail.Chart.Value);
        Assert.Equal(5000, detail.Chart.Baseline);
        Assert.DoesNotContain(detail.Chart.Series, p => p.Value == 88);
        Assert.Equal("Yesterday", detail.Comparison!.CurrentLabel);
        Assert.Contains("2,500", detail.Comparison.CurrentValue);
        Assert.Equal("50% below usual", detail.Comparison.ChangeLabel);
    }

    [Fact]
    public void ElevatedHeartRate_PlotsRestingHrNotSteps()
    {
        var alert = MakeAlert(
            AlertType.HeartRate,
            """{"rule":"elevated_heart_rate","restingHeartRate":88,"baselineAvgRestingHeartRate":68}""");
        var logs = new[]
        {
            Log(_today.AddDays(-1), steps: 9000, restingHr: 88),
            Log(_today.AddDays(-2), steps: 8000, restingHr: 70),
        };

        var detail = AlertDetailComposer.Compose(alert, Member(), null, logs, _today, null, null);

        Assert.Equal("restingHeartRate", detail.Chart!.Metric);
        Assert.Equal(7, detail.Chart.Series.Count);
        Assert.Equal(88, detail.Chart.Value);
        Assert.DoesNotContain(detail.Chart.Series, p => p.Value == 9000);
        Assert.Contains("88", detail.Comparison!.CurrentValue);
        Assert.Contains("bpm", detail.Comparison.CurrentValue);
    }

    [Fact]
    public void IrregularSleep_PlotsHoursNotSteps()
    {
        var alert = MakeAlert(
            AlertType.Sleep,
            """{"rule":"irregular_sleep","sleepMinutes":240,"baselineAvgSleepMinutes":420}""");
        var logs = new[] { Log(_today, steps: 5000, sleepMinutes: 240) };

        var detail = AlertDetailComposer.Compose(alert, Member(), null, logs, _today, null, null);

        Assert.Equal("sleep", detail.Chart!.Metric);
        Assert.Equal(4m, detail.Chart.Value);
        Assert.Equal(7m, detail.Chart.Baseline);
        Assert.Contains("hours", detail.Comparison!.CurrentValue);
    }

    [Fact]
    public void DeviceSilence_HasNoChart()
    {
        var alert = MakeAlert(
            AlertType.Inactivity,
            """{"rule":"device_silence","lastDataUtc":"2026-08-14T08:00:00Z"}""");

        var detail = AlertDetailComposer.Compose(
            alert, Member(), null, [Log(_today, steps: 4000)], _today, null, null);

        Assert.Null(detail.Chart);
        Assert.Null(detail.Comparison);
        Assert.Equal(new DateTime(2026, 8, 14, 8, 0, 0, DateTimeKind.Utc), detail.LastDataAt);
    }

    [Fact]
    public void RealtimeHeartRate_UsesTheGranularHourNotDailyLogs()
    {
        var start = new DateTime(2026, 8, 14, 10, 0, 0, DateTimeKind.Utc);
        var samples = new float?[60];
        samples[0] = 70;
        samples[59] = 92;
        var window = new GranularWindow
        {
            CardiMemberId = _memberId,
            FromUtc = start,
            ToUtc = start.AddHours(1),
            MinuteSeries = new Dictionary<GranularMetric, float?[]>
            {
                [GranularMetric.HeartRate] = samples,
                [GranularMetric.Steps] = Enumerable.Repeat<float?>(4000, 60).ToArray(),
            },
        };
        var alert = MakeAlert(
            AlertType.HeartRate,
            """{"rule":"realtime_hr","hrTrendLast":92,"windowStartUtc":"2026-08-14T10:00:00Z","windowEndUtc":"2026-08-14T11:00:00Z"}""");

        var detail = AlertDetailComposer.Compose(
            alert, Member(), null, [Log(_today, steps: 9000, restingHr: 60)], _today, window,
            new PatternBaseline { AvgRestingHeartRate = 68 });

        Assert.Equal("heartRate", detail.Chart!.Metric);
        Assert.Equal("This hour", detail.Chart.WindowLabel);
        Assert.Equal(92, detail.Chart.Value);
        Assert.DoesNotContain(detail.Chart.Series, p => p.Value == 9000);
        Assert.DoesNotContain(detail.Chart.Series, p => p.Value == 4000);
    }

    [Fact]
    public void RealtimeHeartRate_KeepsTheClosingMinuteWhenDownsampling()
    {
        var start = new DateTime(2026, 8, 14, 10, 0, 0, DateTimeKind.Utc);
        var samples = new float?[AlertDetailComposer.GranularMaxPoints * 2];
        samples[0] = 70;
        samples[^2] = 80;
        samples[^1] = 99;
        var window = new GranularWindow
        {
            CardiMemberId = _memberId,
            FromUtc = start,
            ToUtc = start.AddHours(1),
            MinuteSeries = new Dictionary<GranularMetric, float?[]>
            {
                [GranularMetric.HeartRate] = samples,
            },
        };
        var alert = MakeAlert(
            AlertType.HeartRate,
            """{"rule":"realtime_hr","hrTrendLast":99,"windowStartUtc":"2026-08-14T10:00:00Z","windowEndUtc":"2026-08-14T11:00:00Z"}""");

        var detail = AlertDetailComposer.Compose(alert, Member(), null, [], _today, window, null);

        Assert.Equal(99, detail.Chart!.Value);
        Assert.Equal(99, detail.Chart.Series[^1].Value);
        Assert.True(detail.Chart.Series.Count <= AlertDetailComposer.GranularMaxPoints);
    }

    [Fact]
    public void GranularBounds_AlignToWholeUtcHours()
    {
        var bounds = AlertDetailComposer.GranularBounds(
            """{"rule":"realtime_hr","windowStartUtc":"2026-08-14T10:07:00Z","windowEndUtc":"2026-08-14T10:51:00Z"}""");

        Assert.NotNull(bounds);
        Assert.Equal(new DateTime(2026, 8, 14, 10, 0, 0, DateTimeKind.Utc), bounds!.Value.FromUtc);
        Assert.Equal(new DateTime(2026, 8, 14, 11, 0, 0, DateTimeKind.Utc), bounds.Value.ToUtc);
    }

    [Fact]
    public void Compose_CarriesTheAcknowledgerNameAndMemberPhones()
    {
        var alert = MakeAlert(AlertType.Inactivity, """{"rule":"activity_decline","steps":1}""");
        alert.AcknowledgedDate = DateTime.UtcNow;
        alert.AcknowledgedByUserId = Guid.NewGuid();

        var detail = AlertDetailComposer.Compose(
            alert,
            Member(),
            new User { Name = "Sarah Chen" },
            [],
            _today,
            null,
            null);

        Assert.Equal("Sarah Chen", detail.AcknowledgedByName);
        Assert.Equal("acknowledged", detail.Status);
        Assert.Equal("+15550001111", detail.Phone);
        Assert.Equal("+15550002222", detail.EmergencyContactPhone);
    }

    [Fact]
    public void NoMorning_NamesLastMeasuredStepsDay()
    {
        var alert = MakeAlert(
            AlertType.PatternBreak,
            """{"rule":"no_morning_activity","typicalWakeTime":"07:00"}""");
        var logs = new[]
        {
            Log(_today, steps: 0),
            Log(_today.AddDays(-1), steps: 6120),
        };

        var detail = AlertDetailComposer.Compose(alert, Member(), null, logs, _today, null, null);

        Assert.Equal(_today.AddDays(-1), detail.LastActivityOn);
        Assert.Equal("07:00", detail.TypicalWakeTime);
        Assert.Equal("steps", detail.Chart!.Metric);
    }

    // ── The reason icon key ───────────────────────────────────────────────────

    [Theory]
    [InlineData(StatisticalAlertRules.ActivityDeclineRule, AlertType.Inactivity, AlertReasons.Activity)]
    [InlineData(StatisticalAlertRules.NoMorningActivityRule, AlertType.PatternBreak, AlertReasons.Activity)]
    [InlineData(StatisticalAlertRules.LongTermTrendRule, AlertType.Trend, AlertReasons.Activity)]
    [InlineData(StatisticalAlertRules.ElevatedHeartRateRule, AlertType.HeartRate, AlertReasons.Heart)]
    [InlineData(AlertDetailComposer.RealtimeHeartRateRule, AlertType.HeartRate, AlertReasons.Heart)]
    [InlineData(StatisticalAlertRules.IrregularSleepRule, AlertType.Sleep, AlertReasons.Sleep)]
    [InlineData(AlertDetailComposer.DeviceSilenceRule, AlertType.Inactivity, AlertReasons.Device)]
    public void Reason_FollowsTheRuleNotTheSeverity(string rule, AlertType type, string expected) =>
        Assert.Equal(expected, AlertDetailComposer.Reason(rule, type));

    [Theory]
    // A markerless Inactivity row is the old device-silence producer — the watch, not the wearer.
    [InlineData(AlertType.Inactivity, AlertReasons.Device)]
    [InlineData(AlertType.Trend, AlertReasons.Activity)]
    [InlineData(AlertType.HeartRate, AlertReasons.Heart)]
    [InlineData(AlertType.Sleep, AlertReasons.Sleep)]
    [InlineData(AlertType.PatternBreak, AlertReasons.Monitoring)]
    public void Reason_FallsBackToTheAlertTypeForRowsWithoutARule(AlertType type, string expected) =>
        Assert.Equal(expected, AlertDetailComposer.Reason(null, type));

    // ── The day in progress ───────────────────────────────────────────────────

    [Theory]
    [InlineData(StatisticalAlertRules.ActivityDeclineRule, true)]
    [InlineData(StatisticalAlertRules.NoMorningActivityRule, true)]
    [InlineData(StatisticalAlertRules.LongTermTrendRule, true)]
    // Settled figures when reported — the calendar day having hours left does not make them running
    // totals, so neither gets the partial-day treatment.
    [InlineData(StatisticalAlertRules.ElevatedHeartRateRule, false)]
    [InlineData(StatisticalAlertRules.IrregularSleepRule, false)]
    [InlineData(AlertDetailComposer.DeviceSilenceRule, false)]
    public void NeedsElapsedMatch_OnlyForTheStepWindows(string rule, bool expected) =>
        Assert.Equal(expected, AlertDetailComposer.NeedsElapsedMatch(rule));

    [Fact]
    public void ActivityDecline_HeadlineIsTheLastFinishedDay_NotTodaySoFar()
    {
        var alert = MakeAlert(
            AlertType.Inactivity,
            """{"rule":"activity_decline","steps":1477,"baselineAvgSteps":3797}""");
        var logs = new[]
        {
            // Today, still collecting. This is the number that used to be the headline while the
            // comparison card below it reported yesterday — two days, one number apiece.
            Log(_today, steps: 865),
            Log(_today.AddDays(-1), steps: 1477),
            Log(_today.AddDays(-2), steps: 3600),
        };

        var detail = AlertDetailComposer.Compose(alert, Member(), null, logs, _today, null, null);

        Assert.Equal(1477, detail.Chart!.Value);
        Assert.Equal("Yesterday", detail.Chart.ValueLabel);
        Assert.Contains("1,477", detail.Comparison!.CurrentValue);

        var last = detail.Chart.Series[^1];
        Assert.Equal(_today, last.Date);
        Assert.True(last.IsPartial);
        Assert.DoesNotContain(detail.Chart.Series.SkipLast(1), p => p.IsPartial);
    }

    [Fact]
    public void ElevatedHeartRate_MarksNoDayPartial()
    {
        var alert = MakeAlert(
            AlertType.HeartRate,
            """{"rule":"elevated_heart_rate","restingHeartRate":88,"baselineAvgRestingHeartRate":68}""");
        var logs = new[] { Log(_today, restingHr: 88), Log(_today.AddDays(-1), restingHr: 70) };

        var detail = AlertDetailComposer.Compose(alert, Member(), null, logs, _today, null, null);

        Assert.DoesNotContain(detail.Chart!.Series, p => p.IsPartial);
        Assert.Null(detail.Chart.ValueLabel);
        Assert.Equal(88, detail.Chart.Value);
    }

    [Fact]
    public void PartialDayLabel_ComparesTheSameElapsedStretchOfYesterday()
    {
        var alert = MakeAlert(
            AlertType.Inactivity,
            """{"rule":"activity_decline","steps":1477,"baselineAvgSteps":3797}""");
        var logs = new[] { Log(_today, steps: 865), Log(_today.AddDays(-1), steps: 1477) };

        var detail = AlertDetailComposer.Compose(
            alert, Member(), null, logs, _today, null, null,
            new ElapsedSteps(Today: 865, Comparison: 1102));

        var label = detail.Chart!.PartialDayLabel;
        Assert.Contains("865 steps so far today", label);
        Assert.Contains("1,102 by this time yesterday", label);
        // 865 against 1,102 — the honest figure, not the 77% collapse that comparing this
        // half-day against yesterday's whole 3,797 would have produced.
        Assert.Contains("22% below", label);
    }

    [Fact]
    public void PartialDayLabel_SaysOnlyWhatItKnows_WhenYesterdayHasNoMinuteData()
    {
        var alert = MakeAlert(AlertType.Inactivity, """{"rule":"activity_decline","steps":1477}""");
        var logs = new[] { Log(_today, steps: 865), Log(_today.AddDays(-1), steps: 1477) };

        var detail = AlertDetailComposer.Compose(
            alert, Member(), null, logs, _today, null, null,
            new ElapsedSteps(Today: 865, Comparison: null));

        Assert.Equal("865 steps so far today", detail.Chart!.PartialDayLabel);
    }

    [Fact]
    public void PartialDayLabel_IsAbsentWithoutAnElapsedMatch()
    {
        var alert = MakeAlert(AlertType.Inactivity, """{"rule":"activity_decline","steps":1477}""");
        var logs = new[] { Log(_today, steps: 865), Log(_today.AddDays(-1), steps: 1477) };

        var detail = AlertDetailComposer.Compose(alert, Member(), null, logs, _today, null, null);

        Assert.Null(detail.Chart!.PartialDayLabel);
        // The day is still marked partial, so the chart draws it apart even with nothing to
        // compare it against — the one thing that must never happen is plotting it as finished.
        Assert.True(detail.Chart.Series[^1].IsPartial);
    }

    // ── Elapsed-match arithmetic ──────────────────────────────────────────────

    [Fact]
    public void ElapsedMatchFor_SpansMidnightToTheTopOfTheHour_AndTheSameRunYesterday()
    {
        // 13:30 local in a zone one hour ahead of UTC.
        var zone = TimeZoneInfo.CreateCustomTimeZone("test+1", TimeSpan.FromHours(1), "test+1", "test+1");
        var nowUtc = new DateTime(2026, 8, 14, 12, 30, 0, DateTimeKind.Utc);

        var match = AlertDetailComposer.ElapsedMatchFor(nowUtc, zone);

        Assert.NotNull(match);
        Assert.Equal(new DateOnly(2026, 8, 14), match!.Value.Today);
        Assert.Equal(new DateTime(2026, 8, 13, 23, 0, 0, DateTimeKind.Utc), match.Value.TodayFromUtc);
        // The top of the current hour, not now: the comparison is served from hourly rollups.
        Assert.Equal(new DateTime(2026, 8, 14, 12, 0, 0, DateTimeKind.Utc), match.Value.TodayToUtc);
        Assert.Equal(new DateTime(2026, 8, 12, 23, 0, 0, DateTimeKind.Utc), match.Value.ComparisonFromUtc);
        Assert.Equal(new DateTime(2026, 8, 13, 12, 0, 0, DateTimeKind.Utc), match.Value.ComparisonToUtc);
        Assert.Equal(13, match.Value.Hours);

        // Both stretches cover the same number of hours — that is the whole point of the match.
        Assert.Equal(
            match.Value.TodayToUtc - match.Value.TodayFromUtc,
            match.Value.ComparisonToUtc - match.Value.ComparisonFromUtc);
    }

    /// <summary>
    /// The hour count comes from today's real elapsed span, so a clock-change day still compares
    /// two runs of the same length. Deriving it from the local time-of-day instead would project
    /// an end past "now" on a spring-forward day.
    /// </summary>
    [Theory]
    [InlineData("2026-09-06T15:30:00Z")]  // Santiago springs forward
    [InlineData("2026-04-05T15:30:00Z")]  // and falls back
    public void ElapsedMatchFor_NeverProjectsPastNow_AcrossAClockChange(string nowIso)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/Santiago");
        var nowUtc = DateTime.Parse(
            nowIso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();

        var match = AlertDetailComposer.ElapsedMatchFor(nowUtc, zone);

        Assert.NotNull(match);
        Assert.True(match!.Value.TodayToUtc <= nowUtc);
        Assert.Equal(
            match.Value.Hours,
            (int)(match.Value.ComparisonToUtc - match.Value.ComparisonFromUtc).TotalHours);
    }

    [Fact]
    public void ElapsedMatchFor_IsNullBeforeTheFirstWholeLocalHour()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("test+1", TimeSpan.FromHours(1), "test+1", "test+1");
        var midnightLocal = new DateTime(2026, 8, 13, 23, 0, 0, DateTimeKind.Utc);

        Assert.Null(AlertDetailComposer.ElapsedMatchFor(midnightLocal, zone));
        Assert.Null(AlertDetailComposer.ElapsedMatchFor(midnightLocal.AddMinutes(59), zone));
        Assert.NotNull(AlertDetailComposer.ElapsedMatchFor(midnightLocal.AddHours(1), zone));
    }

    [Fact]
    public void StepsOver_SumsOnlyTheHoursThatCarrySamples()
    {
        var hours = new[]
        {
            Rollup(0, sum: 400, samples: 60),
            Rollup(1, sum: 0, samples: 0),
            Rollup(2, sum: 465, samples: 60),
        };

        var (steps, covered) = AlertDetailComposer.StepsOver(hours);

        Assert.Equal(865m, steps);
        // The empty hour is not coverage — it is the gap the coverage gate exists to notice.
        Assert.Equal(2, covered);
    }

    [Fact]
    public void ElapsedStepsFrom_ComparesTwoWellCoveredDays()
    {
        var elapsed = AlertDetailComposer.ElapsedStepsFrom(
            CoveredHours(865m, hours: 12), CoveredHours(1102m, hours: 12), elapsedHours: 12);

        Assert.NotNull(elapsed);
        Assert.Equal(865m, elapsed!.Today);
        Assert.Equal(1102m, elapsed.Comparison);
    }

    /// <summary>
    /// A day the watch spent most of its hours off the wrist is not the same stretch as a day it
    /// was worn throughout. Comparing them would swap one unfair comparison for another.
    /// </summary>
    [Fact]
    public void ElapsedStepsFrom_DropsTheComparison_WhenYesterdayIsBarelyCovered()
    {
        var elapsed = AlertDetailComposer.ElapsedStepsFrom(
            CoveredHours(865m, hours: 12), CoveredHours(400m, hours: 4), elapsedHours: 12);

        Assert.NotNull(elapsed);
        Assert.Equal(865m, elapsed!.Today);
        Assert.Null(elapsed.Comparison);
    }

    [Fact]
    public void ElapsedStepsFrom_IsNull_WhenTodayItselfIsBarelyCovered()
    {
        Assert.Null(AlertDetailComposer.ElapsedStepsFrom(
            CoveredHours(200m, hours: 3), CoveredHours(1102m, hours: 12), elapsedHours: 12));
        Assert.Null(AlertDetailComposer.ElapsedStepsFrom(
            [], CoveredHours(1102m, hours: 12), elapsedHours: 12));
    }

    /// <summary>
    /// <paramref name="hours"/> hours that all count as covered, carrying <paramref name="steps"/>
    /// between them. The whole total sits in the first hour rather than being divided across them:
    /// what the assertions care about is the sum and the coverage count, and an even split would
    /// only add float rounding to every expected value.
    /// </summary>
    private MetricRollupHourly[] CoveredHours(decimal steps, int hours) =>
        Enumerable.Range(0, hours)
            .Select(h => Rollup(h, sum: h == 0 ? (float)steps : 0f, samples: 60))
            .ToArray();

    private MetricRollupHourly Rollup(int hourOffset, float sum, short samples) => new()
    {
        CardiMemberId = _memberId,
        Metric = GranularMetric.Steps,
        HourStartUtc = new DateTime(2026, 8, 14, 0, 0, 0, DateTimeKind.Utc).AddHours(hourOffset),
        Sum = sum,
        SampleCount = samples,
    };

    private Alert MakeAlert(AlertType type, string metricValues) => new()
    {
        Id = Guid.NewGuid(),
        CardiMemberId = _memberId,
        AlertType = type,
        Severity = AlertSeverity.Yellow,
        Title = "Test",
        Message = "Something changed.",
        TriggeredDate = DateTime.UtcNow,
        MetricValues = metricValues,
        IsActive = true,
    };

    private CardiMember Member() => new()
    {
        Id = _memberId,
        Name = "Margaret Doe",
        Phone = "+15550001111",
        EmergencyContactName = "Jane",
        EmergencyContactPhone = "+15550002222",
        IsActive = true,
    };

    private ActivityLog Log(
        DateOnly date, int? steps = null, int? restingHr = null, int? sleepMinutes = null) => new()
    {
        CardiMemberId = _memberId,
        Date = date,
        Steps = steps,
        RestingHeartRate = restingHr,
        SleepMinutes = sleepMinutes,
    };
}
