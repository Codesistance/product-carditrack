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
    /// Months the `DigestEntries` rows are kept — 7, covering the longest history window any plan
    /// sells (Guardian Plus, 180 days) with a month of margin for the partition drop's whole-range
    /// rule.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Raised from 3 for the CardiJournal's Monthbook: at 3 months a subscriber sold 180 days of
    /// history would watch their own monthly entries disappear after about three of them, which
    /// is a promise the storage was quietly not keeping.
    /// </para>
    /// <para>
    /// One value for every tier, rather than retention per plan. The drop is a whole-partition
    /// operation keyed on `LocalDate` — it cannot tell one member's rows from another's inside a
    /// partition — so per-tier retention would mean row-level deletion or per-tier partitions,
    /// which is a great deal of machinery for a fleet still capped at 100 wearers. The cost of
    /// the simpler choice is real and is recorded rather than hidden: a Basic subscriber sold
    /// 30 days of history has their journal entries kept for seven months. Journal text is ~4 KB
    /// an entry, so this is a data-minimisation question, not a storage one — see the DPIA.
    /// </para>
    /// </remarks>
    public int DigestRetentionMonths { get; set; } = 7;

    /// <summary>
    /// Days the `RealtimeAssessments` rows are kept — 90, the llm_design retention for
    /// real-time results, matching the granular source they are derived from.
    /// </summary>
    public int RealtimeRetentionDays { get; set; } = 90;

    /// <summary>
    /// Days the `EnvironmentalReadings` rows are kept — 90, matching the real-time assessments
    /// they sit alongside in severity/trend prompts.
    /// </summary>
    public int EnvironmentalRetentionDays { get; set; } = 90;
}
