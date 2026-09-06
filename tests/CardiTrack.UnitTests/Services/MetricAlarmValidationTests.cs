using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Enums;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// Pins what an alarm is allowed to be. Most of these are alarm-fatigue guards rather than
/// tidiness: an unbounded threshold field is how a caregiver ends up paged nightly by an ordinary
/// sleeping heart and turns alarms off altogether.
/// </summary>
public class MetricAlarmValidationTests
{
    private static SaveMetricAlarmRequest Valid() => new()
    {
        Name = "Heart rate high",
        Metric = AlarmMetric.HeartRate,
        Statistic = AlarmStatistic.Average,
        Operator = AlarmOperator.GreaterThan,
        ThresholdKind = AlarmThresholdKind.Absolute,
        ThresholdValue = 120m,
        PeriodMinutes = 5,
        EvaluationPeriods = 2,
        DatapointsToAlarm = 2,
        Severity = AlertSeverity.Orange,
    };

    private static IReadOnlyList<AlarmValidationError> Errors(Action<SaveMetricAlarmRequest> tweak)
    {
        var request = Valid();
        tweak(request);
        return MetricAlarmValidation.Validate(request);
    }

    [Fact]
    public void AWellFormedAlarm_Passes()
    {
        Assert.Empty(MetricAlarmValidation.Validate(Valid()));
    }

    [Fact]
    public void Name_IsRequired()
    {
        Assert.Contains(Errors(r => r.Name = "  "), e => e.Field == nameof(SaveMetricAlarmRequest.Name));
    }

    [Fact]
    public void DailyMetric_RefusesASubDailyPeriod()
    {
        // Resting heart rate exists once a day. A five-minute window over it asks for a form of the
        // reading that does not exist.
        var errors = Errors(r =>
        {
            r.Metric = AlarmMetric.RestingHeartRate;
            r.Statistic = AlarmStatistic.Latest;
            r.PeriodMinutes = 5;
        });

        Assert.Contains(errors, e => e.Field == nameof(SaveMetricAlarmRequest.PeriodMinutes));
    }

    [Fact]
    public void LevelMetric_RefusesATotal()
    {
        // A sum of heart rates is a number with no physical meaning.
        Assert.Contains(
            Errors(r => r.Statistic = AlarmStatistic.Sum),
            e => e.Field == nameof(SaveMetricAlarmRequest.Statistic));
    }

    [Fact]
    public void AbsoluteThreshold_MustSitInsideTheMetricsBand()
    {
        Assert.Contains(
            Errors(r => r.ThresholdValue = 400m),
            e => e.Field == nameof(SaveMetricAlarmRequest.ThresholdValue));
    }

    [Fact]
    public void DatapointsToAlarm_CannotExceedTheNumberOfReadingsLookedAt()
    {
        var errors = Errors(r =>
        {
            r.EvaluationPeriods = 2;
            r.DatapointsToAlarm = 3;
        });

        Assert.Contains(errors, e => e.Field == nameof(SaveMetricAlarmRequest.DatapointsToAlarm));
    }

    [Fact]
    public void SubDailyWindow_CannotStretchPastADay()
    {
        var errors = Errors(r =>
        {
            r.PeriodMinutes = 60;
            r.EvaluationPeriods = 12;
            r.DatapointsToAlarm = 1;
        });

        // 60 x 12 is 720 minutes, comfortably inside a day.
        Assert.DoesNotContain(errors, e => e.Field == nameof(SaveMetricAlarmRequest.EvaluationPeriods));
    }

    [Fact]
    public void BaselineRelative_IsRefusedOnAMetricWithNoLearnedUsual()
    {
        // Blood oxygen has no baseline figure on PatternBaseline, so there is nothing to be a
        // percentage of. Better to refuse it than to resolve to nothing at evaluation time.
        var errors = Errors(r =>
        {
            r.Metric = AlarmMetric.SpO2;
            r.ThresholdKind = AlarmThresholdKind.BaselinePercent;
            r.ThresholdValue = 90m;
        });

        Assert.Contains(errors, e => e.Field == nameof(SaveMetricAlarmRequest.ThresholdKind));
    }

    [Fact]
    public void SigmaThreshold_IsRefusedOnAMetricWithNoStoredVariation()
    {
        // Sleep carries an average but no standard deviation.
        var errors = Errors(r =>
        {
            r.Metric = AlarmMetric.SleepMinutes;
            r.Statistic = AlarmStatistic.Latest;
            r.PeriodMinutes = 1440;
            r.ThresholdKind = AlarmThresholdKind.BaselineSigma;
            r.ThresholdValue = 2m;
        });

        Assert.Contains(errors, e => e.Field == nameof(SaveMetricAlarmRequest.ThresholdKind));
    }

    [Fact]
    public void SigmaThreshold_IsAcceptedOnAMetricThatCarriesOne()
    {
        var errors = Errors(r =>
        {
            r.Metric = AlarmMetric.RestingHeartRate;
            r.Statistic = AlarmStatistic.Latest;
            r.PeriodMinutes = 1440;
            r.EvaluationPeriods = 3;
            r.DatapointsToAlarm = 2;
            r.ThresholdKind = AlarmThresholdKind.BaselineSigma;
            r.ThresholdValue = 2m;
        });

        Assert.Empty(errors);
    }

    [Fact]
    public void StillnessGate_IsRefusedOnADailyMetric()
    {
        var errors = Errors(r =>
        {
            r.Metric = AlarmMetric.RestingHeartRate;
            r.Statistic = AlarmStatistic.Latest;
            r.PeriodMinutes = 1440;
            r.ContextGate = AlarmContextGate.Inactive;
        });

        Assert.Contains(errors, e => e.Field == nameof(SaveMetricAlarmRequest.ContextGate));
    }

    [Fact]
    public void RedSeverity_NeedsAnExplicitConfirmation()
    {
        // Red pushes through quiet hours and escalates to other carers. A mis-tapped severity must
        // not be able to wake a family at three in the morning.
        var errors = Errors(r => r.Severity = AlertSeverity.Red);

        Assert.Contains(errors, e => e.Field == nameof(SaveMetricAlarmRequest.Severity));

        Assert.Empty(Errors(r =>
        {
            r.Severity = AlertSeverity.Red;
            r.ConfirmCriticalSeverity = true;
        }));
    }

    [Fact]
    public void GreenSeverity_IsNotOfferedToCaregivers()
    {
        // Green means "we looked and it was fine" — CardiTrack's own finding, not an alarm state.
        Assert.Contains(
            Errors(r => r.Severity = AlertSeverity.Green),
            e => e.Field == nameof(SaveMetricAlarmRequest.Severity));
    }
}
