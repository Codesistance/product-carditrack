using CardiTrack.Domain.Enums;

namespace CardiTrack.Domain.Entities;

/// <summary>
/// One member's hourly summary of one metric, merged across their devices at write time — the hour
/// horizon of the rollup ladder (hour here, day in <see cref="ActivityLog"/>, week/month derived
/// from day). Readers that need "what did this hour look like" come here instead of unpacking
/// minute vectors.
/// <para>
/// Composite-keyed (CardiMemberId, Metric, HourStartUtc) for the same partitioning reason as
/// <see cref="GranularMetricHour"/>; the table is month-partitioned to match its longer retention.
/// </para>
/// </summary>
public class MetricRollupHourly
{
    public Guid CardiMemberId { get; set; }
    public GranularMetric Metric { get; set; }

    /// <summary>Start of the hour, UTC. The partition key.</summary>
    public DateTime HourStartUtc { get; set; }

    public float Min { get; set; }
    public float Max { get; set; }
    public float Avg { get; set; }

    /// <summary>
    /// Sum over the hour — meaningful for count-like metrics (steps, active-zone minutes),
    /// redundant but harmless for level-like ones (heart rate, SpO2).
    /// </summary>
    public float Sum { get; set; }

    public short SampleCount { get; set; }
}
