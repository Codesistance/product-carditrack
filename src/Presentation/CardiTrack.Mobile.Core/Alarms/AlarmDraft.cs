using System.Globalization;
using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Mobile.Core.Alarms;

/// <summary>
/// An alarm being built, and the option lists that go with it. Lives here rather than in the page
/// so the part with rules in it can be tested without a MAUI host.
/// <para>
/// The point of the class is that <b>an illegal alarm should be unreachable rather than refused</b>.
/// The server's catalogue says which statistics mean anything on a metric, how long a datapoint may
/// cover, and what band a threshold must sit in; this narrows the pickers to that as the caregiver
/// moves through them, and re-picks a sane default whenever an earlier choice invalidates a later
/// one. A caregiver who changes the metric should not have to discover that their period is no
/// longer allowed by pressing Save.
/// </para>
/// </summary>
public sealed class AlarmDraft
{
    private readonly AlarmCatalogueResponse _catalogue;

    public AlarmDraft(AlarmCatalogueResponse catalogue, MetricAlarmResponse? existing = null)
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        _catalogue = catalogue;

        if (existing is not null)
        {
            Request = new SaveMetricAlarmRequest
            {
                Name = existing.Name,
                Metric = existing.Metric,
                Statistic = existing.Statistic,
                Operator = existing.Operator,
                ThresholdKind = existing.ThresholdKind,
                ThresholdValue = existing.ThresholdValue,
                PeriodMinutes = existing.PeriodMinutes,
                EvaluationPeriods = existing.EvaluationPeriods,
                DatapointsToAlarm = existing.DatapointsToAlarm,
                MissingDataTreatment = existing.MissingDataTreatment,
                Severity = existing.Severity,
                ContextGate = existing.ContextGate,
                IsEnabled = existing.IsEnabled,

                // A red alarm that is already saved was confirmed when it was made red. Asking again
                // on every edit that leaves it red would train the caregiver to tap past the
                // warning; picking red afresh still asks, because the page resets this on a change.
                ConfirmCriticalSeverity = existing.Severity == AlertSeverity.Red,
            };

            // Narrow the saved alarm against today's catalogue rather than trusting it. It was
            // legal when it was saved, but the catalogue ships with the app and the alarm lives in
            // the database, so a release that drops a statistic or tightens a bound leaves rows
            // behind that no longer validate. Without this the picker would show the first allowed
            // option while Request still held the old one — the screen and the payload disagreeing,
            // and the server rejecting a combination the caregiver never chose. SelectMetric
            // re-picks only what is no longer allowed, so a still-valid alarm is untouched.
            SelectMetric(Request.Metric);
            return;
        }

        Request = new SaveMetricAlarmRequest();
        var first = _catalogue.Metrics.FirstOrDefault();
        if (first is not null)
            SelectMetric(first.Metric);
    }

    public SaveMetricAlarmRequest Request { get; }

    public IReadOnlyList<AlarmMetricOptionResponse> Metrics => _catalogue.Metrics;

    public AlarmMetricOptionResponse? Definition =>
        _catalogue.Metrics.FirstOrDefault(m => m.Metric == Request.Metric);

    public IReadOnlyList<AlarmStatistic> Statistics => Definition?.Statistics ?? [];

    public IReadOnlyList<int> Periods => Definition?.PeriodMinutes ?? [];

    /// <summary>How the threshold may be expressed for the chosen metric. Absolute is always
    /// available; the baseline-relative kinds only where CardiTrack has learned a usual to compare
    /// against.</summary>
    public IReadOnlyList<AlarmThresholdKind> ThresholdKinds
    {
        get
        {
            var kinds = new List<AlarmThresholdKind> { AlarmThresholdKind.Absolute };
            if (Definition?.SupportsBaselinePercent == true)
                kinds.Add(AlarmThresholdKind.BaselinePercent);
            if (Definition?.SupportsBaselineSigma == true)
                kinds.Add(AlarmThresholdKind.BaselineSigma);
            return kinds;
        }
    }

    public bool SupportsContextGate => Definition?.SupportsContextGate == true;

    /// <summary>The range the threshold slider must stay inside, for the chosen threshold kind.</summary>
    public (decimal Min, decimal Max) ThresholdRange => Request.ThresholdKind switch
    {
        AlarmThresholdKind.BaselinePercent =>
            (MetricAlarmValidation.MinPercentThreshold, MetricAlarmValidation.MaxPercentThreshold),
        AlarmThresholdKind.BaselineSigma =>
            (MetricAlarmValidation.MinSigmaThreshold, MetricAlarmValidation.MaxSigmaThreshold),
        _ => (Definition?.MinThreshold ?? 0m, Definition?.MaxThreshold ?? 0m),
    };

    /// <summary>The unit shown beside the threshold — "%" and "×" for the baseline-relative kinds,
    /// since those numbers are not in the metric's own unit.</summary>
    public string ThresholdUnit => Request.ThresholdKind switch
    {
        AlarmThresholdKind.BaselinePercent => "%",
        AlarmThresholdKind.BaselineSigma => "×",
        _ => Definition?.Unit ?? string.Empty,
    };

    /// <summary>How many readings may be looked at, given the chosen metric and period.</summary>
    public int MaxEvaluationPeriods
    {
        get
        {
            var ceiling = _catalogue.MaxEvaluationPeriods;
            if (Definition?.Source == AlarmMetricSource.Granular && Request.PeriodMinutes > 0)
                ceiling = Math.Min(ceiling, 1440 / Request.PeriodMinutes);
            return Math.Max(1, ceiling);
        }
    }

    /// <summary>Switches metric and re-picks every dependent choice, because most of them do not
    /// survive the change: a daily metric has no five-minute period, and a level metric has no total.</summary>
    public void SelectMetric(AlarmMetric metric)
    {
        Request.Metric = metric;
        var definition = Definition;
        if (definition is null)
            return;

        if (!definition.Statistics.Contains(Request.Statistic))
            Request.Statistic = definition.Statistics.FirstOrDefault();

        if (!definition.PeriodMinutes.Contains(Request.PeriodMinutes))
            Request.PeriodMinutes = definition.PeriodMinutes.FirstOrDefault();

        if (!ThresholdKinds.Contains(Request.ThresholdKind))
            Request.ThresholdKind = AlarmThresholdKind.Absolute;

        if (!definition.SupportsContextGate)
            Request.ContextGate = AlarmContextGate.None;

        ClampThreshold();
        ClampCounts();
    }

    public void SelectThresholdKind(AlarmThresholdKind kind)
    {
        if (!ThresholdKinds.Contains(kind))
            return;

        Request.ThresholdKind = kind;
        ClampThreshold();
    }

    public void SelectPeriod(int periodMinutes)
    {
        if (!Periods.Contains(periodMinutes))
            return;

        Request.PeriodMinutes = periodMinutes;
        ClampCounts();
    }

    public void SelectEvaluationPeriods(int periods)
    {
        Request.EvaluationPeriods = Math.Clamp(periods, 1, MaxEvaluationPeriods);
        ClampCounts();
    }

    public void SelectDatapointsToAlarm(int datapoints) =>
        Request.DatapointsToAlarm = Math.Clamp(datapoints, 1, Request.EvaluationPeriods);

    /// <summary>The condition in one sentence, as the saved alarm will carry it.</summary>
    public string Describe() => MetricAlarmNarrative.Condition(Request);

    /// <summary>
    /// Takes the threshold as typed. The caregiver's own culture first — a numeric keypad in a
    /// comma-decimal locale offers a comma — with invariant as a fallback so a pasted "0.5" works
    /// anywhere. Text that is not a number (an emptied field, a lone minus or point mid-edit) leaves
    /// the last good value in place for the preview but marks the draft unsaveable until a number is
    /// there again: the alternative is Save quietly submitting a level the field no longer shows.
    /// </summary>
    /// <returns>Whether the text was a number.</returns>
    public bool SetThresholdText(string? text)
    {
        var parsed = decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out var value)
            || decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out value);

        if (parsed)
            Request.ThresholdValue = value;
        _thresholdUnreadable = !parsed;
        return parsed;
    }

    private bool _thresholdUnreadable;

    /// <summary>Whatever is still wrong with the draft, judged by the same rules the server uses —
    /// plus the one thing only the screen can know, that the level field does not hold a number.</summary>
    public IReadOnlyList<AlarmValidationError> Validate()
    {
        var errors = MetricAlarmValidation.Validate(Request);
        if (!_thresholdUnreadable)
            return errors;

        return
        [
            new AlarmValidationError(nameof(Request.ThresholdValue), "Enter a number for the level."),
            .. errors.Where(e => e.Field != nameof(Request.ThresholdValue)),
        ];
    }

    /// <summary>
    /// Whether the caregiver should be warned rather than stopped — a red alarm pushes through
    /// quiet hours and escalates to other carers, which is worth a deliberate confirmation.
    /// </summary>
    public bool NeedsCriticalConfirmation =>
        Request.Severity == AlertSeverity.Red && !Request.ConfirmCriticalSeverity;

    private void ClampThreshold()
    {
        var (min, max) = ThresholdRange;
        if (max <= min)
            return;

        // A fresh draft has no threshold yet; start it somewhere inside the band rather than at
        // zero, which for most metrics is outside it.
        if (Request.ThresholdValue < min || Request.ThresholdValue > max)
            Request.ThresholdValue = Math.Clamp(Midpoint(min, max), min, max);
    }

    private void ClampCounts()
    {
        Request.EvaluationPeriods = Math.Clamp(Request.EvaluationPeriods, 1, MaxEvaluationPeriods);
        Request.DatapointsToAlarm = Math.Clamp(Request.DatapointsToAlarm, 1, Request.EvaluationPeriods);
    }

    private static decimal Midpoint(decimal min, decimal max) => decimal.Round((min + max) / 2m, 0);
}
