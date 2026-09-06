using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// Pins the user-defined alarm engine to its boundaries. Three things here are load-bearing and
/// each has its own block below: the M-of-N count (breaches need not be adjacent), the missing-data
/// verbs (absence is never silently a breach), and transition-only firing (a standing alarm must
/// not re-page on every tick of a five-minute cron).
/// </summary>
public class MetricAlarmEvaluatorTests
{
    private static MetricAlarm Alarm(
        AlarmMetric metric = AlarmMetric.HeartRate,
        AlarmOperator op = AlarmOperator.GreaterThan,
        decimal threshold = 120m,
        AlarmThresholdKind kind = AlarmThresholdKind.Absolute,
        int evaluationPeriods = 1,
        int datapointsToAlarm = 1,
        AlarmMissingDataTreatment missing = AlarmMissingDataTreatment.Missing) => new()
    {
        Name = "Heart rate high",
        Metric = metric,
        Statistic = AlarmStatistic.Average,
        Operator = op,
        ThresholdKind = kind,
        ThresholdValue = threshold,
        PeriodMinutes = 5,
        EvaluationPeriods = evaluationPeriods,
        DatapointsToAlarm = datapointsToAlarm,
        MissingDataTreatment = missing,
        Severity = AlertSeverity.Orange,
    };

    private static AlarmDatapoint[] Points(params double?[] values) =>
        values.Select(v => new AlarmDatapoint(v)).ToArray();

    private static PatternBaseline Baseline() => new()
    {
        CardiMemberId = Guid.NewGuid(),
        PeriodDays = 30,
        AvgSteps = 6000,
        StdDevSteps = 900,
        AvgRestingHeartRate = 62,
        StdDevHeartRate = 2.0m,
        AvgSleepMinutes = 420,
    };

    // ── operator boundaries ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(AlarmOperator.GreaterThan, 120.1, AlarmEvaluationState.Alarm)]
    [InlineData(AlarmOperator.GreaterThan, 120.0, AlarmEvaluationState.Ok)]
    [InlineData(AlarmOperator.GreaterThanOrEqualTo, 120.0, AlarmEvaluationState.Alarm)]
    [InlineData(AlarmOperator.GreaterThanOrEqualTo, 119.9, AlarmEvaluationState.Ok)]
    [InlineData(AlarmOperator.LessThan, 119.9, AlarmEvaluationState.Alarm)]
    [InlineData(AlarmOperator.LessThan, 120.0, AlarmEvaluationState.Ok)]
    [InlineData(AlarmOperator.LessThanOrEqualTo, 120.0, AlarmEvaluationState.Alarm)]
    [InlineData(AlarmOperator.LessThanOrEqualTo, 120.1, AlarmEvaluationState.Ok)]
    public void Operator_IsExactAtTheBoundary(AlarmOperator op, double value, AlarmEvaluationState expected)
    {
        var verdict = MetricAlarmEvaluator.Evaluate(
            Alarm(op: op), Points(value), null, AlarmEvaluationState.Ok);

        Assert.Equal(expected, verdict.State);
    }

    // ── M of N ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void MOfN_Fires_WhenBreachesAreNotConsecutive()
    {
        // Two bad readings either side of a good one is the signal; demanding adjacency misses it.
        var verdict = MetricAlarmEvaluator.Evaluate(
            Alarm(evaluationPeriods: 3, datapointsToAlarm: 2),
            Points(130, 100, 130),
            null,
            AlarmEvaluationState.Ok);

        Assert.Equal(AlarmEvaluationState.Alarm, verdict.State);
        Assert.Equal(2, verdict.BreachingDatapoints);
    }

    [Fact]
    public void MOfN_StaysQuiet_OneShortOfTheCount()
    {
        var verdict = MetricAlarmEvaluator.Evaluate(
            Alarm(evaluationPeriods: 3, datapointsToAlarm: 3),
            Points(130, 100, 130),
            null,
            AlarmEvaluationState.Ok);

        Assert.Equal(AlarmEvaluationState.Ok, verdict.State);
    }

    // ── missing data ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Missing_WholeWindowAbsent_ReportsInsufficientDataRatherThanAlarm()
    {
        // The case that matters: the watch is off the wrist. Absence must never read as a breach.
        var verdict = MetricAlarmEvaluator.Evaluate(
            Alarm(evaluationPeriods: 3, datapointsToAlarm: 1),
            Points(null, null, null),
            null,
            AlarmEvaluationState.Ok);

        Assert.Equal(AlarmEvaluationState.InsufficientData, verdict.State);
        Assert.False(verdict.RaisedNow);
    }

    [Fact]
    public void Missing_SomeDataPresent_JudgesOnWhatIsThere()
    {
        var verdict = MetricAlarmEvaluator.Evaluate(
            Alarm(evaluationPeriods: 3, datapointsToAlarm: 1),
            Points(null, 130, null),
            null,
            AlarmEvaluationState.Ok);

        Assert.Equal(AlarmEvaluationState.Alarm, verdict.State);
    }

    [Fact]
    public void NotBreaching_CountsAbsenceAsWithinTheThreshold()
    {
        var verdict = MetricAlarmEvaluator.Evaluate(
            Alarm(evaluationPeriods: 3, datapointsToAlarm: 2,
                missing: AlarmMissingDataTreatment.NotBreaching),
            Points(130, null, null),
            null,
            AlarmEvaluationState.Ok);

        Assert.Equal(AlarmEvaluationState.Ok, verdict.State);
    }

    [Fact]
    public void Ignore_HoldsThePreviousState_WhenNothingWasMeasured()
    {
        // A wearer who takes the watch off mid-episode has not thereby ended the episode.
        var verdict = MetricAlarmEvaluator.Evaluate(
            Alarm(evaluationPeriods: 2, datapointsToAlarm: 1, missing: AlarmMissingDataTreatment.Ignore),
            Points(null, null),
            null,
            AlarmEvaluationState.Alarm);

        Assert.Equal(AlarmEvaluationState.Alarm, verdict.State);
        Assert.False(verdict.RaisedNow);
    }

    // ── transition-only firing, and hysteresis ───────────────────────────────────────────

    [Fact]
    public void RaisedNow_IsTrueOnlyOnEntryIntoAlarm()
    {
        var alarm = Alarm();

        var entering = MetricAlarmEvaluator.Evaluate(alarm, Points(130), null, AlarmEvaluationState.Ok);
        var standing = MetricAlarmEvaluator.Evaluate(alarm, Points(130), null, AlarmEvaluationState.Alarm);

        Assert.True(entering.RaisedNow);
        Assert.Equal(AlarmEvaluationState.Alarm, standing.State);
        Assert.False(standing.RaisedNow);
    }

    [Fact]
    public void RaisedNow_IsFalseWhileTheEpisodesAlertIsStillOutstanding()
    {
        // Alarm -> InsufficientData -> Alarm is one episode with a gap in it, not two episodes. The
        // engine passes armed: false for as long as the alert it wrote has not been re-armed by a
        // return to Ok, and the evaluator must honour that even though the state did change.
        var verdict = MetricAlarmEvaluator.Evaluate(
            Alarm(), Points(130), null, AlarmEvaluationState.InsufficientData, armed: false);

        Assert.Equal(AlarmEvaluationState.Alarm, verdict.State);
        Assert.False(verdict.RaisedNow);
    }

    [Fact]
    public void RaisedNow_IsTrueFromInsufficientData_WhenArmed()
    {
        // A new alarm whose first ticks saw no data fires on the first breach it does see.
        var verdict = MetricAlarmEvaluator.Evaluate(
            Alarm(), Points(130), null, AlarmEvaluationState.InsufficientData);

        Assert.True(verdict.RaisedNow);
    }

    [Fact]
    public void Hysteresis_KeepsAStandingAlarmStanding_JustInsideTheThreshold()
    {
        // 118 is under the 120 that fired it, but not under the 114 it has to reach to clear.
        var standing = MetricAlarmEvaluator.Evaluate(
            Alarm(), Points(118), null, AlarmEvaluationState.Alarm);

        Assert.Equal(AlarmEvaluationState.Alarm, standing.State);
    }

    [Fact]
    public void Hysteresis_ClearsOnceTheValueHasProperlyComeBack()
    {
        var cleared = MetricAlarmEvaluator.Evaluate(
            Alarm(), Points(113), null, AlarmEvaluationState.Alarm);

        Assert.Equal(AlarmEvaluationState.Ok, cleared.State);
    }

    [Fact]
    public void Hysteresis_DoesNotWidenTheThresholdForAnAlarmThatIsNotStanding()
    {
        var quiet = MetricAlarmEvaluator.Evaluate(
            Alarm(), Points(118), null, AlarmEvaluationState.Ok);

        Assert.Equal(AlarmEvaluationState.Ok, quiet.State);
    }

    // ── context gate ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void ContextGate_MeasuredButMoving_IsNotABreachAndIsNotMissing()
    {
        // 130 bpm on a staircase is what a working heart looks like. We measured it, we know it
        // does not count, and knowing that is different from having no reading at all.
        var verdict = MetricAlarmEvaluator.Evaluate(
            Alarm(),
            new[] { new AlarmDatapoint(130, GateSatisfied: false) },
            null,
            AlarmEvaluationState.Ok);

        Assert.Equal(AlarmEvaluationState.Ok, verdict.State);
        Assert.Equal(0, verdict.BreachingDatapoints);
        Assert.Equal(1, verdict.EvaluatedDatapoints);
    }

    // ── baseline-relative thresholds ─────────────────────────────────────────────────────

    [Fact]
    public void BaselinePercent_ResolvesAgainstTheMembersOwnAverage()
    {
        // 70% of a 6,000-step usual is 4,200.
        var alarm = Alarm(AlarmMetric.DailySteps, AlarmOperator.LessThan, 70m, AlarmThresholdKind.BaselinePercent);

        var verdict = MetricAlarmEvaluator.Evaluate(alarm, Points(4199), Baseline(), AlarmEvaluationState.Ok);

        Assert.Equal(4200m, verdict.EffectiveThreshold);
        Assert.Equal(AlarmEvaluationState.Alarm, verdict.State);
    }

    [Fact]
    public void BaselineSigma_TakesItsSignFromTheOperator()
    {
        // Resting HR usual 62, sigma 2.0. Two sigma above is 66; two below is 58.
        var above = MetricAlarmEvaluator.Evaluate(
            Alarm(AlarmMetric.RestingHeartRate, AlarmOperator.GreaterThan, 2m, AlarmThresholdKind.BaselineSigma),
            Points(67), Baseline(), AlarmEvaluationState.Ok);

        var below = MetricAlarmEvaluator.Evaluate(
            Alarm(AlarmMetric.RestingHeartRate, AlarmOperator.LessThan, 2m, AlarmThresholdKind.BaselineSigma),
            Points(57), Baseline(), AlarmEvaluationState.Ok);

        Assert.Equal(66m, above.EffectiveThreshold);
        Assert.Equal(58m, below.EffectiveThreshold);
        Assert.Equal(AlarmEvaluationState.Alarm, above.State);
        Assert.Equal(AlarmEvaluationState.Alarm, below.State);
    }

    [Fact]
    public void BaselineRelative_WithNoBaseline_ReportsInsufficientDataAndNeverFires()
    {
        // The provisional-never-alerts principle, reached through the front door: the engine fetches
        // only the established 30-day row, so a member without one arrives here with null.
        var verdict = MetricAlarmEvaluator.Evaluate(
            Alarm(AlarmMetric.DailySteps, AlarmOperator.LessThan, 70m, AlarmThresholdKind.BaselinePercent),
            Points(10),
            null,
            AlarmEvaluationState.Ok);

        Assert.Equal(AlarmEvaluationState.InsufficientData, verdict.State);
        Assert.False(verdict.RaisedNow);
        Assert.Null(verdict.EffectiveThreshold);
    }

    [Fact]
    public void BaselineSigma_OnAMetricWithNoStoredSigma_ReportsInsufficientData()
    {
        // Sleep carries an average but no standard deviation. The catalogue says so; the evaluator
        // must not quietly treat the absence as zero, which would make the threshold the mean.
        var verdict = MetricAlarmEvaluator.Evaluate(
            Alarm(AlarmMetric.SleepMinutes, AlarmOperator.LessThan, 2m, AlarmThresholdKind.BaselineSigma),
            Points(120),
            Baseline(),
            AlarmEvaluationState.Ok);

        Assert.Equal(AlarmEvaluationState.InsufficientData, verdict.State);
    }
}
