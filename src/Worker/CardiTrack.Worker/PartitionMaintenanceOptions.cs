namespace CardiTrack.Worker;

public class PartitionMaintenanceOptions
{
    /// <summary>
    /// How far ahead partitions are pre-created. A week of headroom means the tables survive a
    /// worker outage of several days without an insert ever hitting a missing partition.
    /// </summary>
    public int DaysAhead { get; set; } = 7;

    /// <summary>
    /// Days the minute-grain `GranularMetricHours` rows are kept — 90, aligned with the AI
    /// pipeline's `realtime_results` retention (granular-storage ADR).
    /// </summary>
    public int GranularRetentionDays { get; set; } = 90;

    /// <summary>
    /// Months the `MetricRollupsHourly` rows are kept — 13: a year of hour-grain comparisons
    /// plus a month of slack (granular-storage ADR).
    /// </summary>
    public int RollupRetentionMonths { get; set; } = 13;

    /// <summary>
    /// Months the `DigestEntries` rows are kept — 12, the llm_design retention for digests.
    /// Derived data: regenerable in principle, though the source window ages out first.
    /// </summary>
    public int DigestRetentionMonths { get; set; } = 12;

    /// <summary>
    /// Days the `RealtimeAssessments` rows are kept — 90, the llm_design retention for
    /// real-time results, matching the granular source they are derived from.
    /// </summary>
    public int RealtimeRetentionDays { get; set; } = 90;
}
