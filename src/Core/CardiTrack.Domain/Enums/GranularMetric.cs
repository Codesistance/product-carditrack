namespace CardiTrack.Domain.Enums;

/// <summary>
/// Metrics stored at sub-daily grain. Deliberately a subset of the daily columns on
/// <see cref="Entities.ActivityLog"/>: only the series the SSA-LSTM pre-processing in
/// docs/llm_design.md consumes are worth minute-level storage — everything else stays daily.
/// </summary>
public enum GranularMetric
{
    /// <summary>Heart rate, bpm — 1-minute samples; the primary SSA decomposition series.</summary>
    HeartRate = 1,

    /// <summary>Steps per minute — activity context feature alongside heart rate.</summary>
    Steps = 2,

    /// <summary>Active zone minutes — 1-minute samples; exogenous LSTM input.</summary>
    ActiveZoneMinutes = 3,

    /// <summary>Blood oxygen saturation, % — ~5-minute samples, stored sparse in the minute grid.</summary>
    SpO2 = 4,

    /// <summary>
    /// Heart rate variability (RMSSD), ms — the sub-daily twin of the nightly figure on
    /// <see cref="Entities.ActivityLog.HeartRateVariabilityMs"/>, and the secondary SSA series
    /// docs/llm_design.md has named since the original design. Sampled sparsely (~5-minute at
    /// best, and only while the wearer is still enough to measure), so most minutes of the grid
    /// are empty by nature rather than by omission.
    /// </summary>
    HeartRateVariability = 5,
}
