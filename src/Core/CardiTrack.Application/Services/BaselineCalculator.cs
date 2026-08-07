using System.Text.Json;
using CardiTrack.Domain.Entities;

namespace CardiTrack.Application.Services;

/// <summary>
/// Turns a CardiMember's daily <see cref="ActivityLog"/> history into a <see cref="PatternBaseline"/> —
/// the statistical picture of "a normal day" that <c>DashboardService</c> colours today's metrics
/// against and that alerting will eventually threshold on.
/// <para>
/// Pure and stateless: the job that drives it lives in <c>CardiTrack.Worker</c>, so everything here
/// is unit-testable without a database or a clock.
/// </para>
/// </summary>
public static class BaselineCalculator
{
    /// <summary>Windows a baseline is calculated over, longest last — the caller fetches once for the longest.</summary>
    public static readonly IReadOnlyList<int> SupportedPeriods = [30, 60, 90];

    /// <summary>
    /// Fraction of the window that must carry data before a baseline is written at all. A member who
    /// misses the odd sync day still gets one; a member with three days of data does not — and until
    /// they do, <c>DashboardService</c> keeps reporting the learning state rather than scoring days
    /// against an average of almost nothing.
    /// </summary>
    private const decimal RequiredCoverage = 0.8m;

    /// <summary>
    /// Per-metric floor. Coverage is measured over days with *any* data, but ingestion populates
    /// metrics unevenly (a member may have steps every day and sleep on half of them), so each
    /// metric is gated on its own sample count and left null when it is too thin to average.
    /// </summary>
    private const int MinimumSamplesPerMetric = 7;

    /// <summary>
    /// Weekday buckets see only ~4 samples in a 30-day window, so they carry their own lighter floor.
    /// </summary>
    private const int MinimumSamplesPerWeekday = 2;

    /// <summary>
    /// Mean resultant length below which a set of clock times is too scattered to have a "typical"
    /// value — a member whose bedtimes are spread around the clock gets null, not a meaningless mean.
    /// </summary>
    private const double MinimumTimeConcentration = 0.3;

    private const int MinutesPerDay = 24 * 60;
    private const int DaysPerWeek = 7;

    /// <summary>
    /// Calculates the baseline for one member over one period, or null when the window is too sparse.
    /// </summary>
    /// <param name="logs">
    /// Any daily logs for the member; entries outside the window are ignored, so the caller can fetch
    /// the longest window once and pass the same list for every period.
    /// </param>
    /// <param name="today">The last day of the window — passed in rather than read from the clock.</param>
    public static PatternBaseline? Calculate(
        Guid cardiMemberId, IReadOnlyList<ActivityLog> logs, int periodDays, DateOnly today)
    {
        if (periodDays <= 0)
            throw new ArgumentOutOfRangeException(nameof(periodDays), periodDays, "Period must be positive.");

        var windowStart = today.AddDays(-(periodDays - 1));

        // Ingestion upserts per (DeviceConnection, Date), so a member wearing two devices has two rows
        // for the same day. Collapse to the most recently written row per date — the same rule
        // DashboardService applies, so the baseline and the value compared against it agree.
        var daily = logs
            .Where(l => l.Date >= windowStart && l.Date <= today)
            .GroupBy(l => l.Date)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(l => l.UpdatedDate ?? l.CreatedDate).First());

        var requiredDays = (int)Math.Ceiling(periodDays * RequiredCoverage);
        if (daily.Count < requiredDays)
            return null;

        var days = daily.Values.ToList();

        var steps = Samples(days, l => l.Steps);
        var restingHeartRate = Samples(days, l => l.RestingHeartRate);

        return new PatternBaseline
        {
            CardiMemberId = cardiMemberId,
            PeriodDays = periodDays,

            AvgSteps = MeanAsInt(steps),
            StdDevSteps = StandardDeviation(steps),
            AvgActiveMinutes = MeanAsInt(Samples(days, l => l.ActiveMinutes)),

            AvgRestingHeartRate = MeanAsInt(restingHeartRate),
            StdDevHeartRate = StandardDeviation(restingHeartRate),
            // An observed maximum is a fact rather than an estimate, so it is reported from whatever
            // readings exist instead of being gated on a sample count.
            MaxHeartRateObserved = days.Select(l => l.MaxHeartRate).Where(v => v.HasValue).Max(),

            AvgSleepMinutes = MeanAsInt(Samples(days, l => l.SleepMinutes)),
            TypicalBedtime = TypicalTime(days, l => l.SleepStartTime),
            TypicalWakeTime = TypicalTime(days, l => l.SleepEndTime),
            AvgSleepEfficiency = MeanAsInt(Samples(days, l => l.SleepEfficiency)),

            StepsByDayOfWeek = StepsByDayOfWeek(days),
        };
    }

    private static List<decimal> Samples(IEnumerable<ActivityLog> logs, Func<ActivityLog, decimal?> selector) =>
        logs.Select(selector).Where(v => v.HasValue).Select(v => v!.Value).ToList();

    private static decimal? Mean(IReadOnlyList<decimal> samples) =>
        samples.Count < MinimumSamplesPerMetric ? null : samples.Average();

    private static int? MeanAsInt(IReadOnlyList<decimal> samples) =>
        Mean(samples) is decimal mean ? (int)Math.Round(mean, MidpointRounding.AwayFromZero) : null;

    /// <summary>
    /// Sample standard deviation (n−1). The dashboard turns this into the member's "normal range"
    /// (avg ± σ), so the population form would quietly narrow that band on every member.
    /// </summary>
    private static decimal? StandardDeviation(IReadOnlyList<decimal> samples)
    {
        if (samples.Count < MinimumSamplesPerMetric)
            return null;

        var mean = samples.Average();
        var variance = samples.Sum(v => (v - mean) * (v - mean)) / (samples.Count - 1);
        return Math.Round((decimal)Math.Sqrt((double)variance), 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Circular mean of a set of clock times. Bedtimes straddle midnight, and an arithmetic mean of
    /// 23:40 and 00:20 is midday — so each time is projected onto the 24-hour circle, the vectors are
    /// averaged, and the result is converted back.
    /// <para>
    /// Times are UTC, as stored: <see cref="CardiMember"/> carries no timezone, so converting to the
    /// member's local clock is a presentation concern for whoever displays this.
    /// </para>
    /// </summary>
    private static TimeOnly? TypicalTime(IEnumerable<ActivityLog> logs, Func<ActivityLog, DateTime?> selector)
    {
        var times = logs
            .Select(selector)
            .Where(v => v.HasValue)
            .Select(v => TimeOnly.FromDateTime(v!.Value))
            .ToList();

        if (times.Count < MinimumSamplesPerMetric)
            return null;

        double x = 0, y = 0;
        foreach (var time in times)
        {
            var angle = time.ToTimeSpan().TotalMinutes / MinutesPerDay * 2 * Math.PI;
            x += Math.Cos(angle);
            y += Math.Sin(angle);
        }

        if (Math.Sqrt((x * x) + (y * y)) / times.Count < MinimumTimeConcentration)
            return null;

        var meanAngle = Math.Atan2(y, x);
        if (meanAngle < 0)
            meanAngle += 2 * Math.PI;

        var minutes = Math.Round(meanAngle / (2 * Math.PI) * MinutesPerDay) % MinutesPerDay;
        return TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(minutes));
    }

    /// <summary>
    /// Average steps per weekday as a Monday-first JSON array, e.g. <c>[5200,4800,...,3200]</c>.
    /// Weekdays without enough samples are null rather than zero — "no data for Sundays" and
    /// "this member does not move on Sundays" must not read the same to anything consuming it.
    /// </summary>
    private static string? StepsByDayOfWeek(IReadOnlyList<ActivityLog> days)
    {
        var averages = new int?[DaysPerWeek];
        var any = false;

        for (var index = 0; index < DaysPerWeek; index++)
        {
            // Index 0 is Monday; DayOfWeek starts its week on Sunday.
            var dayOfWeek = (DayOfWeek)((index + 1) % DaysPerWeek);
            var samples = days
                .Where(l => l.Date.DayOfWeek == dayOfWeek)
                .Select(l => l.Steps)
                .Where(v => v.HasValue)
                .Select(v => (decimal)v!.Value)
                .ToList();

            if (samples.Count < MinimumSamplesPerWeekday)
                continue;

            averages[index] = (int)Math.Round(samples.Average(), MidpointRounding.AwayFromZero);
            any = true;
        }

        return any ? JsonSerializer.Serialize(averages) : null;
    }
}
