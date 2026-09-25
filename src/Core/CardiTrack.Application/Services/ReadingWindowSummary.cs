using System.Globalization;
using CardiTrack.Application.DTOs.Common;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Domain.Entities;

namespace CardiTrack.Application.Services;

/// <summary>
/// One metric across a chat readings window, summarised in code: how many days carried it, their
/// average, the lowest and highest day, and the two yardsticks the average is read against.
/// </summary>
/// <param name="Metric">Which reading this is.</param>
/// <param name="DaysConsidered">
/// The days of the window this metric can fairly be averaged over. Every day for an overnight
/// reading; every day but today for a daytime total, because today's is still accumulating.
/// </param>
/// <param name="Readings">The days inside <paramref name="DaysConsidered"/> that carried a reading, oldest first.</param>
/// <param name="Usual">The member's own learned average, when there is one.</param>
/// <param name="Band">The published typical range, when one is published for this reading.</param>
public sealed record ReadingWindowSummary(
    ChartMetricKind Metric,
    int DaysConsidered,
    IReadOnlyList<(DateOnly Day, decimal Value)> Readings,
    decimal? Usual,
    MetricReference? Band)
{
    /// <summary>
    /// How many of <see cref="DaysConsidered"/> must carry a reading before an average of them
    /// means anything — four of seven, the Weekbook's bar, scaled to the window.
    /// </summary>
    public int RequiredDays => ReadingWindowSummaries.RequiredDays(DaysConsidered);

    /// <summary>Whether enough days carried the reading to average them.</summary>
    public bool IsCovered => Readings.Count >= RequiredDays;

    /// <summary>The average of <see cref="Readings"/>, or null when the window is not covered.</summary>
    public decimal? Average => IsCovered ? Readings.Average(r => r.Value) : null;

    /// <summary>The lowest day, or null when nothing was read.</summary>
    public (DateOnly Day, decimal Value)? Lowest =>
        Readings.Count == 0 ? null : Readings.MinBy(r => r.Value);

    /// <summary>The highest day, or null when nothing was read.</summary>
    public (DateOnly Day, decimal Value)? Highest =>
        Readings.Count == 0 ? null : Readings.MaxBy(r => r.Value);
}

/// <summary>
/// The arithmetic a chat answer about a stretch of days rests on, done here rather than by the
/// clinical model.
/// </summary>
/// <remarks>
/// <para>
/// The failure this exists for, from dev on 2026-09-25: asked "how did he sleep this week?", member
/// chat answered twice with an average of about 2h 22m to 2.5h a night, under a chart of the same
/// fetch whose nights ran 4h 18m to 7h. Nothing in the pipeline computed the week's average — the
/// clinical read was handed seven daily rows and left to add them up, on a 4B model quantised to
/// four bits. The journal books had already learned this (a model asked to average seven figures
/// will sometimes average six; <c>JournalPeriodSections.MetricJson</c>), and chat had not.
/// </para>
/// <para>
/// The coverage bar is the same one: four days of seven, scaled. An average over fewer days speaks
/// for the days that are missing, and the answer to a question about a week that mostly did not
/// reach us is the days that did, named — which is what <see cref="MemberChatReplies"/> says in
/// code when no metric the question asked about clears the bar.
/// </para>
/// <para>
/// Which days count follows the rule every renderer of these rows states: a night's sleep and the
/// other overnight readings belong to the morning they ended on, so today's row already holds a
/// finished night; today's steps and resting heart rate are a day still in progress, and averaging
/// them in would read as a collapse the member is not having.
/// </para>
/// </remarks>
public static class ReadingWindowSummaries
{
    /// <summary>The Weekbook's coverage bar: four measured days of every seven.</summary>
    private const decimal CoverageShare = 4m / 7m;

    /// <summary>
    /// The metrics in the order the charts draw them, so the prompt and the reply list them the
    /// same way the caregiver sees them.
    /// </summary>
    private static readonly ChartMetricKind[] Order =
    [
        ChartMetricKind.Steps,
        ChartMetricKind.RestingHeartRate,
        ChartMetricKind.Sleep,
        ChartMetricKind.HeartRateVariability,
        ChartMetricKind.OvernightBreathingRate,
    ];

    /// <summary>
    /// The days of <paramref name="daysConsidered"/> that must carry a reading. Rounded up, so a
    /// six-day window needs four, not three.
    /// </summary>
    public static int RequiredDays(int daysConsidered) =>
        Math.Max(1, (int)Math.Ceiling(daysConsidered * CoverageShare));

    /// <summary>
    /// Whether a reading is taken overnight and so belongs, finished, to the morning it ended on.
    /// </summary>
    public static bool IsOvernight(ChartMetricKind metric) => metric is
        ChartMetricKind.Sleep or ChartMetricKind.HeartRateVariability or ChartMetricKind.OvernightBreathingRate;

    /// <summary>
    /// One summary per metric the window carried at least one reading of, or that the question
    /// asked about, in chart order. Empty when the window is a single day — there is nothing to
    /// average across one.
    /// </summary>
    /// <param name="ageYears">Picks the sleep band's ceiling; null draws no sleep band rather than a guessed one.</param>
    /// <param name="askedMetrics">
    /// The readings the question is about, summarised even when no day carried them. A metric the
    /// member's device never reports is otherwise left out, as the daily rows leave it out — but
    /// one the question asked for has to be stated as missing, or the model answers it from the
    /// baseline.
    /// </param>
    public static IReadOnlyList<ReadingWindowSummary> For(
        IReadOnlyList<ActivityLog> rows,
        (DateOnly From, DateOnly To) window,
        DateOnly today,
        PatternBaseline? baseline,
        int? ageYears,
        IReadOnlyList<ChartMetricKind>? askedMetrics = null)
    {
        var summaries = new List<ReadingWindowSummary>();
        foreach (var metric in Order)
        {
            var summary = ForMetric(metric, rows, window, today, baseline, ageYears);
            if (summary is not null
                && (summary.Readings.Count > 0 || askedMetrics?.Contains(metric) == true))
            {
                summaries.Add(summary);
            }
        }

        return summaries;
    }

    /// <summary>
    /// One metric's summary, or null when the window holds fewer than two days it could be
    /// averaged over. Returned even when no day carried the reading, so a caller asking about a
    /// specific metric can say how many days it had against how many it needed.
    /// </summary>
    public static ReadingWindowSummary? ForMetric(
        ChartMetricKind metric,
        IReadOnlyList<ActivityLog> rows,
        (DateOnly From, DateOnly To) window,
        DateOnly today,
        PatternBaseline? baseline,
        int? ageYears)
    {
        // Today's daytime totals are still accumulating; everything else in the window is a day
        // or a night that has finished.
        var last = !IsOvernight(metric) && window.To >= today ? today.AddDays(-1) : window.To;
        var considered = last.DayNumber - window.From.DayNumber + 1;
        if (considered < 2)
            return null;

        var readings = rows
            .Where(l => l.Date >= window.From && l.Date <= last)
            .Select(l => (Day: l.Date, Value: Value(metric, l)))
            .Where(r => r.Value.HasValue)
            .OrderBy(r => r.Day)
            .Select(r => (r.Day, r.Value!.Value))
            .ToList();

        return new ReadingWindowSummary(metric, considered, readings, Usual(metric, baseline), Band(metric, ageYears));
    }

    /// <summary>The reading as a caregiver names it, for the middle of a sentence.</summary>
    public static string Name(ChartMetricKind metric) => metric switch
    {
        ChartMetricKind.Steps => "step count",
        ChartMetricKind.RestingHeartRate => "resting heart rate",
        ChartMetricKind.Sleep => "sleep",
        ChartMetricKind.HeartRateVariability => "overnight heart rate variability",
        ChartMetricKind.OvernightBreathingRate => "overnight breathing rate",
        _ => metric.ToString(),
    };

    /// <summary>
    /// One figure of <paramref name="metric"/> in its own unit, rounded the way the charts label
    /// it — a night in hours and minutes, never a bare count of minutes.
    /// </summary>
    public static string Figure(ChartMetricKind metric, decimal value) => metric switch
    {
        ChartMetricKind.Steps => string.Create(CultureInfo.InvariantCulture, $"{Math.Round(value):#,##0} steps"),
        ChartMetricKind.RestingHeartRate => string.Create(CultureInfo.InvariantCulture, $"{Math.Round(value):0} bpm"),
        ChartMetricKind.Sleep => ReadingFigures.SleepFigure((int)Math.Round(value, MidpointRounding.AwayFromZero)),
        ChartMetricKind.HeartRateVariability => string.Create(CultureInfo.InvariantCulture, $"{Math.Round(value):0} ms"),
        ChartMetricKind.OvernightBreathingRate => string.Create(CultureInfo.InvariantCulture, $"{value:0.#} breaths a minute"),
        _ => value.ToString(CultureInfo.InvariantCulture),
    };

    private static decimal? Value(ChartMetricKind metric, ActivityLog log) => metric switch
    {
        ChartMetricKind.Steps => log.Steps,
        ChartMetricKind.RestingHeartRate => log.RestingHeartRate,
        ChartMetricKind.Sleep => log.SleepMinutes,
        ChartMetricKind.HeartRateVariability => log.HeartRateVariabilityMs,
        ChartMetricKind.OvernightBreathingRate => log.OvernightBreathingRate,
        _ => null,
    };

    private static decimal? Usual(ChartMetricKind metric, PatternBaseline? baseline) => metric switch
    {
        ChartMetricKind.Steps => baseline?.AvgSteps,
        ChartMetricKind.RestingHeartRate => baseline?.AvgRestingHeartRate,
        ChartMetricKind.Sleep => baseline?.AvgSleepMinutes,
        ChartMetricKind.HeartRateVariability => baseline?.AvgHeartRateVariabilityMs,
        ChartMetricKind.OvernightBreathingRate => baseline?.AvgOvernightBreathingRate,
        _ => null,
    };

    /// <summary>
    /// The published band in the metric's own unit — sleep's hours become minutes, because the
    /// readings are minutes. Steps and overnight HRV have none: no accredited body publishes one.
    /// Nor does breathing asleep: WHO's 12–20 is a waking rate at rest, and graded against it an
    /// overnight figure would carry WHO's name for a comparison WHO never made
    /// (<see cref="HealthReferenceRanges.NoOvernightBreathingBand"/>).
    /// </summary>
    private static MetricReference? Band(ChartMetricKind metric, int? ageYears) => metric switch
    {
        ChartMetricKind.RestingHeartRate => HealthReferenceRanges.RestingHeartRate,
        ChartMetricKind.Sleep when ageYears is { } age => SleepBandInMinutes(age),
        _ => null,
    };

    private static MetricReference SleepBandInMinutes(int ageYears)
    {
        var band = HealthReferenceRanges.Sleep(ageYears);
        return new MetricReference { Low = band.Low * 60, High = band.High * 60, Source = band.Source };
    }
}
