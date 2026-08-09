using CardiTrack.Domain.Enums;

namespace CardiTrack.Domain.Entities;

/// <summary>
/// One device's samples for one metric over one UTC hour, as a fixed 60-slot minute vector —
/// the raw grain behind <see cref="MetricRollupHourly"/> the same way <see cref="DeviceActivityLog"/>
/// is the raw grain behind <see cref="ActivityLog"/>. Rows are per-device and merged on read
/// (<see cref="GranularSeriesMerge"/>) rather than merged on write: a 10-minute sync fills an hour
/// in slices, and rewriting a merged row on every slice would multiply write traffic for nothing.
/// <para>
/// Deliberately not a <see cref="Common.BaseEntity"/>: the table is range-partitioned on
/// <see cref="HourStartUtc"/>, PostgreSQL requires the partition key inside the primary key, and a
/// surrogate Guid buys nothing on an append-only series. The natural key is
/// (DeviceConnectionId, Metric, HourStartUtc).
/// </para>
/// </summary>
public class GranularMetricHour
{
    public const int MinutesPerHour = 60;

    public Guid DeviceConnectionId { get; set; }
    public Guid CardiMemberId { get; set; }
    public GranularMetric Metric { get; set; }

    /// <summary>Start of the hour, UTC, minute/second zero. The partition key.</summary>
    public DateTime HourStartUtc { get; set; }

    /// <summary>
    /// 60 slots, index = minute of the hour; null means the device reported no sample for that
    /// minute. Sparse-cadence metrics (SpO2 at ~5-minute intervals) sit sparse in the same grid —
    /// one shape everywhere beats per-metric slot counts.
    /// </summary>
    public float?[] Values { get; set; } = new float?[MinutesPerHour];

    /// <summary>Non-null slots in <see cref="Values"/> — a cheap emptiness and quality check.</summary>
    public short SampleCount { get; set; }
}
