using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.ExternalClients;

namespace CardiTrack.Infrastructure.Services;

/// <summary>
/// Pure arithmetic between the provider's raw timestamped readings and the stored shapes: raw
/// samples → per-device hour vectors, and a merged window → per-member hourly rollups. Kept free
/// of I/O so both directions are unit-testable without a provider or a database.
/// </summary>
public static class GranularDayBucketer
{
    /// <summary>
    /// Buckets one device's day of readings into hour vectors. Metric semantics decide how two
    /// readings landing in the same minute combine: <em>additive</em> quantities (steps,
    /// active-zone minutes — the latter arrives once per heart-rate zone) sum, because both
    /// happened in that minute; <em>level</em> measurements (heart rate, SpO2) take the latest
    /// reading, because a level has one value at a time and the samples are processed in time
    /// order.
    /// </summary>
    public static IReadOnlyList<GranularMetricHour> Bucket(
        Guid cardiMemberId, Guid deviceConnectionId, DeviceGranularDay day)
    {
        var rows = new Dictionary<(GranularMetric Metric, DateTime HourStartUtc), GranularMetricHour>();

        AddSeries(day.HeartRate, GranularMetric.HeartRate, additive: false);
        AddSeries(day.Steps, GranularMetric.Steps, additive: true);
        AddSeries(day.ActiveZoneMinutes, GranularMetric.ActiveZoneMinutes, additive: true);
        AddSeries(day.SpO2, GranularMetric.SpO2, additive: false);
        // Level, not additive: two RMSSD readings in one minute are two estimates of the same
        // quantity, and summing them would report double the variability the wearer has.
        AddSeries(day.HeartRateVariability, GranularMetric.HeartRateVariability, additive: false);

        foreach (var row in rows.Values)
            row.SampleCount = (short)row.Values.Count(v => v.HasValue);

        return [.. rows.Values];

        void AddSeries(IReadOnlyList<GranularSample> samples, GranularMetric metric, bool additive)
        {
            foreach (var sample in samples.OrderBy(s => s.TimeUtc))
            {
                var hourStart = new DateTime(
                    sample.TimeUtc.Ticks - (sample.TimeUtc.Ticks % TimeSpan.TicksPerHour),
                    DateTimeKind.Utc);

                if (!rows.TryGetValue((metric, hourStart), out var row))
                {
                    row = new GranularMetricHour
                    {
                        CardiMemberId = cardiMemberId,
                        DeviceConnectionId = deviceConnectionId,
                        Metric = metric,
                        HourStartUtc = hourStart,
                    };
                    rows[(metric, hourStart)] = row;
                }

                var minute = sample.TimeUtc.Minute;
                row.Values[minute] = additive
                    ? (row.Values[minute] ?? 0f) + sample.Value
                    : sample.Value;
            }
        }
    }

    /// <summary>
    /// Hourly rollups from a member's <em>merged</em> minute window — merged, not per-device,
    /// because a rollup describes the member's hour and single-device statistics would overwrite
    /// it with a partial view. Hours with no data in a metric produce no row: an absent hour and a
    /// measured hour must stay distinguishable downstream, the same null-versus-zero rule the
    /// daily pipeline enforces.
    /// </summary>
    public static IReadOnlyList<MetricRollupHourly> RollupsFromWindow(GranularWindow window)
    {
        var rollups = new List<MetricRollupHourly>();
        var hourCount = (int)((window.ToUtc - window.FromUtc).TotalMinutes / GranularMetricHour.MinutesPerHour);

        foreach (var (metric, series) in window.MinuteSeries)
        {
            for (var hourIndex = 0; hourIndex < hourCount; hourIndex++)
            {
                var slice = series
                    .Skip(hourIndex * GranularMetricHour.MinutesPerHour)
                    .Take(GranularMetricHour.MinutesPerHour)
                    .Where(v => v.HasValue)
                    .Select(v => v!.Value)
                    .ToList();

                if (slice.Count == 0)
                    continue;

                rollups.Add(new MetricRollupHourly
                {
                    CardiMemberId = window.CardiMemberId,
                    Metric = metric,
                    HourStartUtc = window.FromUtc.AddHours(hourIndex),
                    Min = slice.Min(),
                    Max = slice.Max(),
                    Avg = slice.Average(),
                    Sum = slice.Sum(),
                    SampleCount = (short)slice.Count,
                });
            }
        }

        return rollups;
    }
}
