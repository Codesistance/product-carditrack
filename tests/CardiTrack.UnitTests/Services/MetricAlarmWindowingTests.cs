using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// Pins where the evaluation window ends and what one period reduces to. The anchoring rule is the
/// non-obvious part: ingestion polls every ten minutes, so anchoring to wall-clock time would leave
/// the newest datapoint permanently missing and make short alarms unfireable.
/// </summary>
public class MetricAlarmWindowingTests
{
    private static readonly DateTime SeriesStart = new(2026, 9, 6, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>Twenty minutes into the series — comfortably inside the lag allowance of the
    /// readings each test plants, so anchoring is about the data rather than about staleness.</summary>
    private static readonly DateTime Now = SeriesStart.AddMinutes(20);

    private static MetricAlarm Alarm(
        AlarmMetric metric = AlarmMetric.HeartRate,
        AlarmStatistic statistic = AlarmStatistic.Average,
        int periodMinutes = 5,
        int evaluationPeriods = 2,
        AlarmContextGate gate = AlarmContextGate.None) => new()
    {
        Name = "Test",
        Metric = metric,
        Statistic = statistic,
        Operator = AlarmOperator.GreaterThan,
        ThresholdKind = AlarmThresholdKind.Absolute,
        ThresholdValue = 120m,
        PeriodMinutes = periodMinutes,
        EvaluationPeriods = evaluationPeriods,
        DatapointsToAlarm = 1,
        ContextGate = gate,
    };

    /// <summary>A 60-slot minute series with <paramref name="values"/> placed at <paramref name="at"/>.</summary>
    private static float?[] Series(int at, params float?[] values)
    {
        var series = new float?[60];
        for (var i = 0; i < values.Length; i++)
            series[at + i] = values[i];
        return series;
    }

    [Fact]
    public void Window_EndsAtTheLastReading_NotAtTheEndOfTheSeries()
    {
        // Readings stop at minute 19; the last 40 minutes of the series are ingestion lag, not a
        // wearer who took the watch off. Both datapoints must land on the data that exists.
        var series = Series(10, 130, 130, 130, 130, 130, 130, 130, 130, 130, 130);

        var points = MetricAlarmWindowing.FromMinuteSeries(Alarm(), series, null, SeriesStart, Now);

        Assert.Equal(2, points.Count);
        Assert.All(points, p => Assert.Equal(130d, p.Value));
    }

    [Fact]
    public void Window_WithNoReadingsAtAll_IsAllMissing()
    {
        var points = MetricAlarmWindowing.FromMinuteSeries(Alarm(), new float?[60], null, SeriesStart, Now);

        Assert.Equal(2, points.Count);
        Assert.All(points, p => Assert.Null(p.Value));
    }

    [Fact]
    public void Window_WithNoSeriesForTheMetric_IsAllMissing()
    {
        var points = MetricAlarmWindowing.FromMinuteSeries(Alarm(), null, null, SeriesStart, Now);

        Assert.Equal(2, points.Count);
        Assert.All(points, p => Assert.Null(p.Value));
    }

    [Fact]
    public void Datapoints_AreOldestFirst()
    {
        // Minutes 10-14 read 100, minutes 15-19 read 130. Newest period is the 130s.
        var series = Series(10, 100, 100, 100, 100, 100, 130, 130, 130, 130, 130);

        var points = MetricAlarmWindowing.FromMinuteSeries(Alarm(), series, null, SeriesStart, Now);

        Assert.Equal(100d, points[0].Value);
        Assert.Equal(130d, points[1].Value);
    }

    [Theory]
    [InlineData(AlarmStatistic.Average, 110d)]
    [InlineData(AlarmStatistic.Minimum, 100d)]
    [InlineData(AlarmStatistic.Maximum, 120d)]
    [InlineData(AlarmStatistic.Sum, 330d)]
    [InlineData(AlarmStatistic.Latest, 110d)]
    public void Statistic_ReducesThePeriodAsNamed(AlarmStatistic statistic, double expected)
    {
        var series = Series(10, 100, 120, 110);

        var points = MetricAlarmWindowing.FromMinuteSeries(
            Alarm(statistic: statistic, evaluationPeriods: 1), series, null, SeriesStart, Now);

        Assert.Equal(expected, points[0].Value);
    }

    [Fact]
    public void Average_SkipsUnmeasuredMinutesRatherThanCountingThemAsZero()
    {
        // The failure this guards against: averaging an unworn watch in as a run of zeroes would
        // drag every level metric toward the floor and fire every low alarm on the estate.
        var series = new float?[60];
        series[10] = 120;
        series[11] = null;
        series[12] = 120;

        var points = MetricAlarmWindowing.FromMinuteSeries(
            Alarm(evaluationPeriods: 1), series, null, SeriesStart, Now);

        Assert.Equal(120d, points[0].Value);
    }

    // ── the stillness gate ───────────────────────────────────────────────────────────────

    [Fact]
    public void Gate_IsSatisfied_WhenStepsWereMeasuredAndWereZero()
    {
        var heart = Series(10, 130, 130, 130, 130, 130);
        var steps = Series(10, 0, 0, 0, 0, 0);

        var points = MetricAlarmWindowing.FromMinuteSeries(
            Alarm(evaluationPeriods: 1, gate: AlarmContextGate.Inactive), heart, steps, SeriesStart, Now);

        Assert.True(points[0].GateSatisfied);
    }

    [Fact]
    public void Gate_Fails_WhenTheyWereMoving()
    {
        var heart = Series(10, 130, 130, 130, 130, 130);
        var steps = Series(10, 0, 40, 55, 60, 30);

        var points = MetricAlarmWindowing.FromMinuteSeries(
            Alarm(evaluationPeriods: 1, gate: AlarmContextGate.Inactive), heart, steps, SeriesStart, Now);

        Assert.False(points[0].GateSatisfied);
    }

    [Fact]
    public void Gate_Fails_WhenThereIsNoStepSeriesToEstablishStillnessFrom()
    {
        // "We do not know" must not read as "they were still" — that is how a gated alarm starts
        // firing on stair climbs.
        var heart = Series(10, 130, 130, 130, 130, 130);

        var points = MetricAlarmWindowing.FromMinuteSeries(
            Alarm(evaluationPeriods: 1, gate: AlarmContextGate.Inactive), heart, null, SeriesStart, Now);

        Assert.False(points[0].GateSatisfied);
    }

    [Fact]
    public void Window_RefusesToAnchorOnAReadingOlderThanTheLagAllowance()
    {
        // The series is fetched on whole-hour bounds, so it reaches back further than the allowance.
        // A watch that stopped reporting over an hour ago must not have those readings evaluated as
        // if they were current — that is stale data raising a live alarm.
        var series = Series(2, 130, 130, 130, 130, 130);
        var wellAfter = SeriesStart.AddMinutes(2 + MetricAlarmWindowing.LagAllowanceMinutes + 5);

        var points = MetricAlarmWindowing.FromMinuteSeries(Alarm(), series, null, SeriesStart, wellAfter);

        Assert.All(points, p => Assert.Null(p.Value));
    }

    [Fact]
    public void Window_StillAnchors_OnAReadingJustInsideTheLagAllowance()
    {
        var series = Series(10, 130, 130, 130, 130, 130);
        var justInside = SeriesStart.AddMinutes(14 + MetricAlarmWindowing.LagAllowanceMinutes);

        var points = MetricAlarmWindowing.FromMinuteSeries(Alarm(), series, null, SeriesStart, justInside);

        Assert.Equal(130d, points[^1].Value);
    }

    [Fact]
    public void Window_IgnoresAMinuteStampedAfterNow()
    {
        // The fetch runs to the end of the current hour, so later slots exist and should be empty.
        // A future-stamped sample must not become "the last reading".
        var series = new float?[60];
        series[10] = 130;
        series[40] = 200;

        var points = MetricAlarmWindowing.FromMinuteSeries(Alarm(), series, null, SeriesStart, Now);

        Assert.DoesNotContain(points, p => p.Value == 200d);
        Assert.Equal(130d, points[^1].Value);
    }

    // ── daily ────────────────────────────────────────────────────────────────────────────

    private static Dictionary<DateOnly, ActivityLog> Logs(params (DateOnly Date, int? Steps)[] rows) =>
        rows.ToDictionary(r => r.Date, r => new ActivityLog { Date = r.Date, Steps = r.Steps });

    [Fact]
    public void Daily_AnchorsOnTheMostRecentDayThatCarriesTheReading()
    {
        // Today is unfinished and has no step row yet; yesterday is the newest datapoint.
        var today = new DateOnly(2026, 9, 6);
        var logs = Logs((today.AddDays(-2), 5000), (today.AddDays(-1), 3000));

        var points = MetricAlarmWindowing.FromDailyLogs(
            Alarm(AlarmMetric.DailySteps, AlarmStatistic.Latest, 1440, 2), logs, today);

        Assert.Equal(5000d, points[0].Value);
        Assert.Equal(3000d, points[1].Value);
    }

    [Fact]
    public void Daily_AReadingThatAccumulatesThroughTheDay_IsJudgedOnTheLastCompletedDay()
    {
        // 07:10, first sync of the morning: today's row already exists and says 120 steps. That is
        // not a day of 120 steps, it is a day that has barely started. A "below 3,000" alarm anchored
        // on it would page every morning of an ordinary life.
        var today = new DateOnly(2026, 9, 6);
        var logs = Logs((today.AddDays(-1), 6500), (today, 120));

        var points = MetricAlarmWindowing.FromDailyLogs(
            Alarm(AlarmMetric.DailySteps, AlarmStatistic.Latest, 1440, 1), logs, today);

        Assert.Equal(6500d, points[0].Value);
    }

    [Fact]
    public void Daily_AReadingThatIsWholeWhenFiled_StillUsesTodaysRow()
    {
        // Resting heart rate is one figure for the day, not a running total, so the freshest row is
        // the right one — the accumulating exception must not leak onto every daily metric.
        var today = new DateOnly(2026, 9, 6);
        var logs = new Dictionary<DateOnly, ActivityLog>
        {
            [today.AddDays(-1)] = new() { Date = today.AddDays(-1), RestingHeartRate = 60 },
            [today] = new() { Date = today, RestingHeartRate = 71 },
        };

        var points = MetricAlarmWindowing.FromDailyLogs(
            Alarm(AlarmMetric.RestingHeartRate, AlarmStatistic.Latest, 1440, 1), logs, today);

        Assert.Equal(71d, points[0].Value);
    }

    [Fact]
    public void Daily_UsesTodaysRow_ForAReadingThatIsFiledOnTheDayTheNightEnded()
    {
        // Sleep is attributed to the civil day the night ended on, so last night lives on today's
        // row. One anchoring rule covers that without the engine knowing which metric settles when.
        var today = new DateOnly(2026, 9, 6);
        var logs = new Dictionary<DateOnly, ActivityLog>
        {
            [today.AddDays(-1)] = new() { Date = today.AddDays(-1), SleepMinutes = 420 },
            [today] = new() { Date = today, SleepMinutes = 250 },
        };

        var points = MetricAlarmWindowing.FromDailyLogs(
            Alarm(AlarmMetric.SleepMinutes, AlarmStatistic.Latest, 1440, 2), logs, today);

        Assert.Equal(420d, points[0].Value);
        Assert.Equal(250d, points[1].Value);
    }

    [Fact]
    public void Daily_WithNothingRecentEnough_IsAllMissing()
    {
        // A watch that has been in a drawer for a week must not have last Tuesday evaluated as now.
        var today = new DateOnly(2026, 9, 6);
        var logs = Logs((today.AddDays(-9), 5000));

        var points = MetricAlarmWindowing.FromDailyLogs(
            Alarm(AlarmMetric.DailySteps, AlarmStatistic.Latest, 1440, 2), logs, today);

        Assert.All(points, p => Assert.Null(p.Value));
    }

    [Fact]
    public void Daily_GapInTheMiddleOfTheRange_IsOneMissingDatapoint()
    {
        var today = new DateOnly(2026, 9, 6);
        var logs = Logs((today.AddDays(-3), 5000), (today.AddDays(-1), 3000));

        var points = MetricAlarmWindowing.FromDailyLogs(
            Alarm(AlarmMetric.DailySteps, AlarmStatistic.Latest, 1440, 3), logs, today);

        Assert.Equal(5000d, points[0].Value);
        Assert.Null(points[1].Value);
        Assert.Equal(3000d, points[2].Value);
    }
}
