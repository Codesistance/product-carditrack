namespace CardiTrack.Infrastructure.ExternalClients;

/// <summary>One timestamped reading. The instant is physical UTC, not civil — it is what the
/// minute-grid bucketing keys on (`GranularMetricHour.HourStartUtc` is UTC by design).</summary>
public sealed record GranularSample(DateTime TimeUtc, float Value);

/// <summary>
/// Provider-neutral sub-daily series for one member for one civil day — the granular counterpart
/// of <see cref="DeviceHealthSnapshot"/>, feeding the `GranularMetricHours` substrate
/// (docs/technical/granular_timeseries_storage.md). Lists are empty, never null, for metrics the
/// device does not record.
/// <para>
/// Samples are raw provider readings, not yet a minute grid: heart rate arrives at ~1-minute
/// cadence, SpO2 and heart-rate variability at ~5-minute, and steps/active-zone-minutes as
/// intervals stamped at their start.
/// Active-zone-minutes may carry several entries for the same instant (one per heart-rate zone) —
/// bucketing sums them, because they are additive quantities earned in the same minute.
/// </para>
/// </summary>
public sealed record DeviceGranularDay(
    IReadOnlyList<GranularSample> HeartRate,
    IReadOnlyList<GranularSample> Steps,
    IReadOnlyList<GranularSample> ActiveZoneMinutes,
    IReadOnlyList<GranularSample> SpO2,
    IReadOnlyList<GranularSample> HeartRateVariability)
{
    public static readonly DeviceGranularDay Empty = new([], [], [], [], []);

    /// <summary>True when any series carries a reading — the same storage gate
    /// <see cref="DeviceHealthSnapshot.HasAnyData"/> serves for day rows.</summary>
    public bool HasAnyData =>
        HeartRate.Count > 0 || Steps.Count > 0 || ActiveZoneMinutes.Count > 0 || SpO2.Count > 0
        || HeartRateVariability.Count > 0;
}
