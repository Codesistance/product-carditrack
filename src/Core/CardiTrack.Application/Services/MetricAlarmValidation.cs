using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Services;

/// <summary>One rejected field and why.</summary>
public sealed record AlarmValidationError(string Field, string Message);

/// <summary>
/// What an alarm is allowed to be. Pure and in the Application layer so the API validator, the
/// service and the tests all judge by the same rules — and so the reasons live next to the
/// catalogue that supplies the numbers rather than being restated in a validator.
/// <para>
/// Most of these are not arbitrary strictness. An unbounded threshold field is an alarm-fatigue
/// generator: the clinical definition of bradycardia is a resting rate under 60, and a caregiver
/// who types that into a low-heart-rate alarm will be paged by an ordinary sleeping heart every
/// night for a week and then turn alarms off altogether. Bounding the field is the same thing
/// Apple and Fitbit do to theirs, and for the same reason.
/// </para>
/// </summary>
public static class MetricAlarmValidation
{
    /// <summary>The most enabled alarms one member may carry. A hard ceiling, well above the
    /// point at which a caregiver stops being able to hold them all in mind.</summary>
    public const int MaxEnabledAlarmsPerMember = 12;

    /// <summary>
    /// Past this the client warns rather than refuses. Six is where the nursing literature on
    /// alarm management puts the limit of what a person reliably keeps track of; more than that
    /// is allowed, but it should be a decision rather than an accident.
    /// </summary>
    public const int RecommendedMaxEnabledAlarms = 6;

    public const int MaxNameLength = 80;

    /// <summary>A percentage-of-baseline threshold outside this band is not a tuning, it is a typo.</summary>
    public const decimal MinPercentThreshold = 1m;
    public const decimal MaxPercentThreshold = 300m;

    /// <summary>Sigma multipliers. Below half a sigma an alarm fires on ordinary day-to-day
    /// variation; past six it can never fire at all.</summary>
    public const decimal MinSigmaThreshold = 0.5m;
    public const decimal MaxSigmaThreshold = 6m;

    public static IReadOnlyList<AlarmValidationError> Validate(SaveMetricAlarmRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = new List<AlarmValidationError>();

        if (string.IsNullOrWhiteSpace(request.Name))
            errors.Add(new(nameof(request.Name), "Give the alarm a name you will recognise."));
        else if (request.Name.Trim().Length > MaxNameLength)
            errors.Add(new(nameof(request.Name), $"Keep the name to {MaxNameLength} characters or fewer."));

        var definition = AlarmMetricCatalogue.Find(request.Metric);
        if (definition is null)
        {
            errors.Add(new(nameof(request.Metric), "That is not a metric CardiTrack can alarm on."));
            return errors;
        }

        if (!definition.Statistics.Contains(request.Statistic))
        {
            errors.Add(new(nameof(request.Statistic),
                $"{definition.Title} does not support that statistic."));
        }

        if (!definition.PeriodMinutes.Contains(request.PeriodMinutes))
        {
            errors.Add(new(nameof(request.PeriodMinutes),
                $"{definition.Title} can only be watched over "
                + $"{string.Join(", ", definition.PeriodMinutes.Select(Describe))}."));
        }

        if (!Enum.IsDefined(request.Operator))
            errors.Add(new(nameof(request.Operator), "Choose how the reading is compared."));

        if (request.EvaluationPeriods is < 1 || request.EvaluationPeriods > AlarmMetricCatalogue.MaxEvaluationPeriods)
        {
            errors.Add(new(nameof(request.EvaluationPeriods),
                $"Look at between 1 and {AlarmMetricCatalogue.MaxEvaluationPeriods} readings."));
        }

        if (request.DatapointsToAlarm < 1 || request.DatapointsToAlarm > request.EvaluationPeriods)
        {
            errors.Add(new(nameof(request.DatapointsToAlarm),
                "The number of readings that must cross the line cannot be more than the number looked at."));
        }

        if (definition.Source == AlarmMetricSource.Granular
            && request.PeriodMinutes > 0
            && request.EvaluationPeriods > 0
            && request.PeriodMinutes * request.EvaluationPeriods > AlarmMetricCatalogue.MaxSubDailyRangeMinutes)
        {
            errors.Add(new(nameof(request.EvaluationPeriods),
                "That window covers more than a day. Use a longer period or fewer readings."));
        }

        ValidateThreshold(request, definition, errors);

        if (request.ContextGate == AlarmContextGate.Inactive && definition.Source != AlarmMetricSource.Granular)
        {
            errors.Add(new(nameof(request.ContextGate),
                "Stillness can only be checked on a short window, where there is a step reading for the same minutes."));
        }
        else if (!Enum.IsDefined(request.ContextGate))
        {
            errors.Add(new(nameof(request.ContextGate), "Choose when the alarm applies."));
        }

        if (!Enum.IsDefined(request.MissingDataTreatment))
            errors.Add(new(nameof(request.MissingDataTreatment), "Choose what a missing reading means."));

        if (!Enum.IsDefined(request.Severity) || request.Severity == AlertSeverity.Green)
        {
            errors.Add(new(nameof(request.Severity),
                "Choose how urgent this alarm is. Green is reserved for CardiTrack's own findings."));
        }
        else if (request.Severity == AlertSeverity.Red && !request.ConfirmCriticalSeverity)
        {
            errors.Add(new(nameof(request.Severity),
                "A red alarm pushes through quiet hours and escalates to other carers. Confirm that is what you want."));
        }

        return errors;
    }

    private static void ValidateThreshold(
        SaveMetricAlarmRequest request, AlarmMetricDefinition definition, List<AlarmValidationError> errors)
    {
        switch (request.ThresholdKind)
        {
            case AlarmThresholdKind.Absolute:
                if (request.ThresholdValue < definition.MinThreshold || request.ThresholdValue > definition.MaxThreshold)
                {
                    errors.Add(new(nameof(request.ThresholdValue),
                        $"Set a level between {definition.MinThreshold:0.#} and {definition.MaxThreshold:0.#} "
                        + $"{definition.Unit}."));
                }
                break;

            case AlarmThresholdKind.BaselinePercent:
                if (!definition.SupportsBaselinePercent)
                {
                    errors.Add(new(nameof(request.ThresholdKind),
                        $"CardiTrack does not learn a usual {definition.Title.ToLowerInvariant()} to compare against. "
                        + "Set a fixed level instead."));
                }
                else if (request.ThresholdValue < MinPercentThreshold || request.ThresholdValue > MaxPercentThreshold)
                {
                    errors.Add(new(nameof(request.ThresholdValue),
                        $"Set a share between {MinPercentThreshold:0}% and {MaxPercentThreshold:0}% of their usual."));
                }
                break;

            case AlarmThresholdKind.BaselineSigma:
                if (!definition.SupportsBaselineSigma)
                {
                    errors.Add(new(nameof(request.ThresholdKind),
                        $"CardiTrack does not learn how much {definition.Title.ToLowerInvariant()} varies for this "
                        + "person, so it cannot measure a departure from usual. Set a fixed level, or a share of "
                        + "their usual, instead."));
                }
                else if (request.ThresholdValue < MinSigmaThreshold || request.ThresholdValue > MaxSigmaThreshold)
                {
                    errors.Add(new(nameof(request.ThresholdValue),
                        $"Set a departure between {MinSigmaThreshold:0.#} and {MaxSigmaThreshold:0.#} times their "
                        + "usual variation."));
                }
                break;

            default:
                errors.Add(new(nameof(request.ThresholdKind), "Choose what the number means."));
                break;
        }
    }

    private static string Describe(int minutes) => minutes switch
    {
        AlarmMetricCatalogue.DailyPeriodMinutes => "a whole day",
        60 => "an hour",
        var m => $"{m} minutes",
    };
}
