using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Services;

/// <summary>
/// Cuts a member's readings into the datapoints one alarm is evaluated over. Pure, so the two
/// decisions that are easy to get quietly wrong — where the window ends, and what one period
/// reduces to — are testable without a database.
/// <para>
/// <b>The window ends at the last reading, not at the clock.</b> Ingestion polls every ten minutes,
/// so the minutes either side of "now" are routinely empty even for a wearer whose watch is on and
/// working. Anchoring the newest period to wall-clock time would make every short alarm with
/// M equal to N unfireable, since the newest datapoint would almost always be missing. Anchoring it
/// to the last minute that actually reported evaluates the data we have. The search for that
/// anchor is bounded — see <see cref="LagAllowanceMinutes"/> — so a wearer whose watch has been in
/// a drawer for a week does not have last Tuesday evaluated as if it were now.
/// </para>
/// </summary>
public static class MetricAlarmWindowing
{
    /// <summary>
    /// How far before the evaluation range the anchor search may reach, to absorb ordinary
    /// ingestion lag. An hour is comfortably more than the ten-minute poll and comfortably less
    /// than the two hours that make device silence its own alert.
    /// </summary>
    public const int LagAllowanceMinutes = 60;

    /// <summary>Extra days fetched beyond the evaluation range when anchoring a daily alarm.</summary>
    public const int DailyLagAllowanceDays = 2;

    /// <summary>How many minutes of series a sub-daily alarm needs fetched for it.</summary>
    public static int RequiredMinutes(MetricAlarm alarm)
    {
        ArgumentNullException.ThrowIfNull(alarm);
        return alarm.PeriodMinutes * alarm.EvaluationPeriods + LagAllowanceMinutes;
    }

    /// <summary>How many days of activity logs a daily alarm needs fetched for it.</summary>
    public static int RequiredDays(MetricAlarm alarm)
    {
        ArgumentNullException.ThrowIfNull(alarm);
        return alarm.EvaluationPeriods + DailyLagAllowanceDays;
    }

    /// <summary>
    /// The alarm's datapoints from a merged minute series, oldest first.
    /// </summary>
    /// <param name="series">The metric's minute values; slot 0 is the minute at <paramref name="seriesStartUtc"/>.</param>
    /// <param name="stepSeries">
    /// The step series over the same minutes, for <see cref="AlarmContextGate.Inactive"/>. Null when
    /// the member's device does not report steps — in which case stillness cannot be established and
    /// no period passes the gate, which is the safe direction: an alarm that only fires when we
    /// positively know the wearer was still is better than one that assumes it.
    /// </param>
    /// <param name="utcNow">
    /// The clock end of the search. The anchor must be a reading no older than
    /// <see cref="LagAllowanceMinutes"/> before this, which is what actually bounds the staleness
    /// this method is allowed to evaluate — the fetched series is widened to whole hours and so
    /// reaches further back than the allowance on its own.
    /// </param>
    public static IReadOnlyList<AlarmDatapoint> FromMinuteSeries(
        MetricAlarm alarm, float?[]? series, float?[]? stepSeries, DateTime seriesStartUtc, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(alarm);

        var empty = Enumerable.Repeat(new AlarmDatapoint(null), alarm.EvaluationPeriods).ToList();
        if (series is null || series.Length == 0)
            return empty;

        var anchor = LastReadingIndex(series, seriesStartUtc, utcNow);
        if (anchor is not { } lastIndex)
            return empty;

        var end = lastIndex + 1;
        var points = new List<AlarmDatapoint>(alarm.EvaluationPeriods);

        for (var k = 0; k < alarm.EvaluationPeriods; k++)
        {
            var periodsBack = alarm.EvaluationPeriods - k;
            var from = end - periodsBack * alarm.PeriodMinutes;
            var to = from + alarm.PeriodMinutes;

            var value = Reduce(series, Math.Max(from, 0), Math.Min(to, series.Length), alarm.Statistic);
            var gated = alarm.ContextGate != AlarmContextGate.Inactive
                || WasStill(stepSeries, Math.Max(from, 0), Math.Min(to, series.Length));

            points.Add(new AlarmDatapoint(value, gated));
        }

        return points;
    }

    /// <summary>
    /// The alarm's datapoints from merged day rows, oldest first. One datapoint is one civil day on
    /// the member's own clock — or one night, for the readings derived from sleep, which the day
    /// rows already file under the day the night ended on.
    /// </summary>
    public static IReadOnlyList<AlarmDatapoint> FromDailyLogs(
        MetricAlarm alarm, IReadOnlyDictionary<DateOnly, ActivityLog> logsByDate, DateOnly localToday)
    {
        ArgumentNullException.ThrowIfNull(alarm);
        ArgumentNullException.ThrowIfNull(logsByDate);

        var empty = Enumerable.Repeat(new AlarmDatapoint(null), alarm.EvaluationPeriods).ToList();

        // Same anchoring rule as the minute series: the newest datapoint is the most recent day
        // that actually carries this reading. Today is usually unfinished and often empty, and for
        // sleep-derived readings the freshest row is today's rather than yesterday's — one rule
        // covers both without the engine having to know which metric settles when.
        DateOnly? anchor = null;
        for (var back = 0; back <= DailyLagAllowanceDays; back++)
        {
            var day = localToday.AddDays(-back);
            if (logsByDate.TryGetValue(day, out var log) && AlarmMetricCatalogue.DailyValue(alarm.Metric, log) is not null)
            {
                anchor = day;
                break;
            }
        }

        if (anchor is not { } newest)
            return empty;

        var points = new List<AlarmDatapoint>(alarm.EvaluationPeriods);
        for (var k = 0; k < alarm.EvaluationPeriods; k++)
        {
            var day = newest.AddDays(-(alarm.EvaluationPeriods - 1 - k));
            var value = logsByDate.TryGetValue(day, out var log)
                ? AlarmMetricCatalogue.DailyValue(alarm.Metric, log)
                : null;
            points.Add(new AlarmDatapoint(value));
        }

        return points;
    }

    /// <summary>
    /// The most recent minute carrying a reading, searched backwards from now and no further than
    /// <see cref="LagAllowanceMinutes"/>.
    /// <para>
    /// Both ends of that search matter. The series is fetched on whole-hour bounds, so it reaches
    /// back further than the allowance and forward to the end of the current hour. Without the
    /// upper bound a future-stamped sample would become "the last reading"; without the lower one,
    /// a watch that stopped reporting two hours ago would still be anchored on and its readings
    /// evaluated as if they were current — which is the staleness the allowance exists to refuse,
    /// and on a health alarm the wrong direction to be wrong in.
    /// </para>
    /// </summary>
    private static int? LastReadingIndex(float?[] series, DateTime seriesStartUtc, DateTime utcNow)
    {
        // Both bounds come from the clock and are only then clamped to the array. Deriving the
        // lower bound from the clamped upper one instead would quietly widen the lookback whenever
        // the series ends before now — which is exactly when a stale anchor is the risk.
        var newest = (int)Math.Floor((utcNow - seriesStartUtc).TotalMinutes);
        if (newest < 0)
            return null;

        var oldest = (int)Math.Ceiling(
            (utcNow.AddMinutes(-LagAllowanceMinutes) - seriesStartUtc).TotalMinutes);

        newest = Math.Min(newest, series.Length - 1);
        oldest = Math.Max(oldest, 0);

        for (var i = newest; i >= oldest; i--)
        {
            if (series[i] is not null)
                return i;
        }
        return null;
    }

    /// <summary>
    /// Reduces the minutes in [from, to) to the single number the threshold is compared against.
    /// Minutes with no sample are skipped rather than counted as zero — averaging an unworn watch
    /// in as a run of zeroes would drag a heart rate toward the floor and fire every low alarm on
    /// the estate.
    /// </summary>
    private static double? Reduce(float?[] series, int from, int to, AlarmStatistic statistic)
    {
        double? min = null, max = null, latest = null;
        var sum = 0d;
        var count = 0;

        for (var i = from; i < to; i++)
        {
            if (series[i] is not { } sample)
                continue;

            count++;
            sum += sample;
            latest = sample;
            min = min is { } lo ? Math.Min(lo, sample) : sample;
            max = max is { } hi ? Math.Max(hi, sample) : sample;
        }

        if (count == 0)
            return null;

        return statistic switch
        {
            AlarmStatistic.Minimum => min,
            AlarmStatistic.Maximum => max,
            AlarmStatistic.Average => sum / count,
            AlarmStatistic.Sum => sum,
            AlarmStatistic.Latest => latest,
            _ => null,
        };
    }

    /// <summary>
    /// Whether the period can be positively established as still. Requires at least one measured
    /// step minute summing to zero: an absent step series proves nothing, and treating "we do not
    /// know" as "they were still" is how a gated alarm starts firing on stair climbs.
    /// </summary>
    private static bool WasStill(float?[]? stepSeries, int from, int to)
    {
        if (stepSeries is null)
            return false;

        var measured = false;
        var total = 0d;

        for (var i = from; i < to && i < stepSeries.Length; i++)
        {
            if (stepSeries[i] is not { } sample)
                continue;

            measured = true;
            total += sample;
        }

        return measured && total == 0d;
    }
}
