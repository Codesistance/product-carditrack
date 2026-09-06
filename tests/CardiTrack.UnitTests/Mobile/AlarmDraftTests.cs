using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Enums;
using CardiTrack.Mobile.Core.Alarms;

namespace CardiTrack.UnitTests.Mobile;

/// <summary>
/// Pins the builder's narrowing. The promise the screen makes is that an illegal alarm is
/// unreachable rather than refused, so what matters here is that changing an early choice re-picks
/// every later one it invalidates — a caregiver must not discover by pressing Save that their
/// period stopped being allowed three taps ago.
/// </summary>
public class AlarmDraftTests
{
    /// <summary>The real catalogue, mapped the way the API maps it, so these tests move when it does.</summary>
    private static AlarmCatalogueResponse Catalogue() => new()
    {
        MaxEvaluationPeriods = AlarmMetricCatalogue.MaxEvaluationPeriods,
        Metrics = AlarmMetricCatalogue.Definitions.Select(d => new AlarmMetricOptionResponse
        {
            Metric = d.Metric,
            Title = d.Title,
            Unit = d.Unit,
            Source = d.Source == AlarmMetricSource.Granular ? "granular" : "daily",
            Statistics = d.Statistics,
            PeriodMinutes = d.PeriodMinutes,
            MinThreshold = d.MinThreshold,
            MaxThreshold = d.MaxThreshold,
            SupportsBaselinePercent = d.SupportsBaselinePercent,
            SupportsBaselineSigma = d.SupportsBaselineSigma,
            SupportsContextGate = d.Source == AlarmMetricSource.Granular,
        }).ToList(),
    };

    private static AlarmDraft Draft() => new(Catalogue());

    [Fact]
    public void ANewDraft_StartsOnAValidCombination()
    {
        var draft = Draft();

        Assert.Contains(draft.Request.Statistic, draft.Statistics);
        Assert.Contains(draft.Request.PeriodMinutes, draft.Periods);

        // Every choice, not just the two the pickers narrow. A draft that arrives with an unset
        // operator fails validation on a question the caregiver was never asked.
        Assert.Equal(
            [nameof(draft.Request.Name)],
            draft.Validate().Select(e => e.Field));
    }

    [Fact]
    public void SwitchingToADailyMetric_RepicksThePeriod()
    {
        var draft = Draft();
        draft.SelectMetric(AlarmMetric.HeartRate);
        draft.SelectPeriod(5);

        draft.SelectMetric(AlarmMetric.RestingHeartRate);

        Assert.Equal(AlarmMetricCatalogue.DailyPeriodMinutes, draft.Request.PeriodMinutes);
    }

    [Fact]
    public void SwitchingToALevelMetric_RepicksAStatisticThatDoesNotSurvive()
    {
        var draft = Draft();
        draft.SelectMetric(AlarmMetric.Steps);
        draft.Request.Statistic = AlarmStatistic.Sum;

        draft.SelectMetric(AlarmMetric.HeartRate);

        Assert.DoesNotContain(AlarmStatistic.Sum, draft.Statistics);
        Assert.Contains(draft.Request.Statistic, draft.Statistics);
    }

    [Fact]
    public void SwitchingAwayFromABaselineCapableMetric_FallsBackToAFixedLevel()
    {
        var draft = Draft();
        draft.SelectMetric(AlarmMetric.RestingHeartRate);
        draft.SelectThresholdKind(AlarmThresholdKind.BaselineSigma);

        draft.SelectMetric(AlarmMetric.SpO2);

        Assert.Equal(AlarmThresholdKind.Absolute, draft.Request.ThresholdKind);
    }

    [Fact]
    public void SwitchingToADailyMetric_DropsTheStillnessGate()
    {
        var draft = Draft();
        draft.SelectMetric(AlarmMetric.HeartRate);
        draft.Request.ContextGate = AlarmContextGate.Inactive;

        draft.SelectMetric(AlarmMetric.RestingHeartRate);

        Assert.Equal(AlarmContextGate.None, draft.Request.ContextGate);
        Assert.False(draft.SupportsContextGate);
    }

    [Fact]
    public void ThresholdKinds_OfferOnlyWhatTheMetricSupports()
    {
        var draft = Draft();

        draft.SelectMetric(AlarmMetric.SpO2);
        Assert.Equal([AlarmThresholdKind.Absolute], draft.ThresholdKinds);

        draft.SelectMetric(AlarmMetric.SleepMinutes);
        Assert.Equal([AlarmThresholdKind.Absolute, AlarmThresholdKind.BaselinePercent], draft.ThresholdKinds);

        draft.SelectMetric(AlarmMetric.RestingHeartRate);
        Assert.Equal(
            [AlarmThresholdKind.Absolute, AlarmThresholdKind.BaselinePercent, AlarmThresholdKind.BaselineSigma],
            draft.ThresholdKinds);
    }

    [Fact]
    public void ThresholdRange_FollowsTheKind_NotJustTheMetric()
    {
        var draft = Draft();
        draft.SelectMetric(AlarmMetric.RestingHeartRate);

        Assert.Equal((30m, 150m), draft.ThresholdRange);

        draft.SelectThresholdKind(AlarmThresholdKind.BaselinePercent);
        Assert.Equal(
            (MetricAlarmValidation.MinPercentThreshold, MetricAlarmValidation.MaxPercentThreshold),
            draft.ThresholdRange);
        Assert.Equal("%", draft.ThresholdUnit);
    }

    [Fact]
    public void ThresholdValue_IsPulledBackInsideTheBandWhenTheKindChanges()
    {
        var draft = Draft();
        draft.SelectMetric(AlarmMetric.RestingHeartRate);
        draft.Request.ThresholdValue = 140m;

        draft.SelectThresholdKind(AlarmThresholdKind.BaselineSigma);

        var (min, max) = draft.ThresholdRange;
        Assert.InRange(draft.Request.ThresholdValue, min, max);
    }

    [Fact]
    public void SubDailyWindow_CannotBeStretchedPastADay()
    {
        var draft = Draft();
        draft.SelectMetric(AlarmMetric.HeartRate);
        draft.SelectPeriod(60);

        draft.SelectEvaluationPeriods(99);

        Assert.True(draft.Request.PeriodMinutes * draft.Request.EvaluationPeriods <= 1440);
        Assert.DoesNotContain(draft.Validate(), e => e.Field == "EvaluationPeriods");
    }

    [Fact]
    public void DatapointsToAlarm_NeverExceedsTheReadingsLookedAt()
    {
        var draft = Draft();
        draft.SelectEvaluationPeriods(4);
        draft.SelectDatapointsToAlarm(4);

        draft.SelectEvaluationPeriods(2);

        Assert.Equal(2, draft.Request.DatapointsToAlarm);
    }

    [Fact]
    public void Describe_ReadsAsASentence()
    {
        var draft = Draft();
        draft.SelectMetric(AlarmMetric.HeartRate);
        draft.Request.Statistic = AlarmStatistic.Average;
        draft.Request.Operator = AlarmOperator.GreaterThan;
        draft.Request.ThresholdValue = 120m;
        draft.SelectPeriod(10);
        draft.SelectEvaluationPeriods(2);
        draft.SelectDatapointsToAlarm(2);
        draft.Request.ContextGate = AlarmContextGate.Inactive;

        Assert.Equal(
            "Average heart rate is above 120 bpm over 10 minutes, on 2 of the last 2, while they are still.",
            draft.Describe());
    }

    [Fact]
    public void EditingAnExistingAlarm_StartsFromWhatWasSaved()
    {
        var existing = new MetricAlarmResponse
        {
            Name = "Night-time low",
            Metric = AlarmMetric.HeartRate,
            Statistic = AlarmStatistic.Average,
            Operator = AlarmOperator.LessThan,
            ThresholdKind = AlarmThresholdKind.Absolute,
            ThresholdValue = 42m,
            PeriodMinutes = 10,
            EvaluationPeriods = 2,
            DatapointsToAlarm = 2,
            Severity = AlertSeverity.Orange,
            ContextGate = AlarmContextGate.Inactive,
            IsEnabled = true,
        };

        var draft = new AlarmDraft(Catalogue(), existing);

        Assert.Equal("Night-time low", draft.Request.Name);
        Assert.Equal(42m, draft.Request.ThresholdValue);
        Assert.Empty(draft.Validate());
    }

    [Fact]
    public void EditingAnAlarmTheCatalogueNoLongerAllows_RepicksInsteadOfShowingAMismatch()
    {
        // The alarm was legal when saved; the catalogue ships with the app and the row lives in the
        // database, so a release can leave rows behind that no longer validate. The draft has to
        // narrow them, or the picker shows the first allowed option while Request still holds the
        // old one and Save fails on a combination the caregiver never chose.
        var existing = new MetricAlarmResponse
        {
            Name = "Stale",
            Metric = AlarmMetric.HeartRate,
            Statistic = AlarmStatistic.Sum,          // never offered on a level metric
            Operator = AlarmOperator.GreaterThan,
            ThresholdKind = AlarmThresholdKind.BaselineSigma, // heart rate has no learned baseline
            ThresholdValue = 9_999m,                 // far outside the band
            PeriodMinutes = 1440,                    // a daily period on a sub-daily metric
            EvaluationPeriods = 99,
            DatapointsToAlarm = 99,
            Severity = AlertSeverity.Orange,
            IsEnabled = true,
        };

        var draft = new AlarmDraft(Catalogue(), existing);

        Assert.Contains(draft.Request.Statistic, draft.Statistics);
        Assert.Contains(draft.Request.PeriodMinutes, draft.Periods);
        Assert.Contains(draft.Request.ThresholdKind, draft.ThresholdKinds);
        var (min, max) = draft.ThresholdRange;
        Assert.InRange(draft.Request.ThresholdValue, min, max);
        Assert.Empty(draft.Validate());
    }

    [Fact]
    public void RedSeverity_AsksForConfirmationBeforeItIsValid()
    {
        var draft = Draft();
        draft.Request.Name = "Blood oxygen critical";
        draft.Request.Severity = AlertSeverity.Red;

        Assert.True(draft.NeedsCriticalConfirmation);

        draft.Request.ConfirmCriticalSeverity = true;

        Assert.False(draft.NeedsCriticalConfirmation);
        Assert.Empty(draft.Validate());
    }
}
