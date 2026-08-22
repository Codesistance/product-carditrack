namespace CardiTrack.Domain.Entities;

/// <summary>
/// Merges a CardiMember's per-device day rows into the single <see cref="ActivityLog"/> row that
/// every reader consumes.
/// <para>
/// Each metric is resolved independently: the first device, in priority order, that reported a
/// non-null value wins. Values are never summed — a watch and a ring worn by the same person both
/// count the same steps, so adding them would double-count. Coalescing instead lets devices fill
/// each other's gaps, which is the actual benefit of wearing more than one (a ring supplies sleep
/// and SpO2, a watch supplies steps and heart rate).
/// </para>
/// <para>
/// Pure and order-independent: callers pass rows already sorted by device priority, and the result
/// depends only on that input, so re-running the merge over the full raw set is always safe.
/// </para>
/// </summary>
public static class ActivityLogMerge
{
    /// <summary>
    /// Builds the merged row for one member-day. <paramref name="rowsByPriority"/> must be ordered
    /// highest-priority device first. Returns null when there is nothing to merge.
    /// </summary>
    public static ActivityLog? Merge(
        Guid cardiMemberId,
        DateOnly date,
        IReadOnlyList<DeviceActivityLog> rowsByPriority)
    {
        if (rowsByPriority.Count == 0)
            return null;

        // Provenance points at the highest-priority device that contributed anything, which is the
        // first row — callers only pass rows that exist for this member-day.
        var primary = rowsByPriority[0];

        return new ActivityLog
        {
            Id = Guid.NewGuid(),
            CardiMemberId = cardiMemberId,
            Date = date,
            DeviceConnectionId = primary.DeviceConnectionId,
            DataSource = primary.DataSource,

            Steps = First(rowsByPriority, r => r.Steps),
            Distance = First(rowsByPriority, r => r.Distance),
            ActiveMinutes = First(rowsByPriority, r => r.ActiveMinutes),
            SedentaryMinutes = First(rowsByPriority, r => r.SedentaryMinutes),
            Floors = First(rowsByPriority, r => r.Floors),
            CaloriesBurned = First(rowsByPriority, r => r.CaloriesBurned),

            RestingHeartRate = First(rowsByPriority, r => r.RestingHeartRate),
            AvgHeartRate = First(rowsByPriority, r => r.AvgHeartRate),
            MaxHeartRate = First(rowsByPriority, r => r.MaxHeartRate),
            MinHeartRate = First(rowsByPriority, r => r.MinHeartRate),

            SleepMinutes = First(rowsByPriority, r => r.SleepMinutes),
            SleepStartTime = First(rowsByPriority, r => r.SleepStartTime),
            SleepEndTime = First(rowsByPriority, r => r.SleepEndTime),
            SleepEfficiency = First(rowsByPriority, r => r.SleepEfficiency),
            DeepSleepMinutes = First(rowsByPriority, r => r.DeepSleepMinutes),
            LightSleepMinutes = First(rowsByPriority, r => r.LightSleepMinutes),
            RemSleepMinutes = First(rowsByPriority, r => r.RemSleepMinutes),
            AwakeMinutes = First(rowsByPriority, r => r.AwakeMinutes),

            SpO2Average = First(rowsByPriority, r => r.SpO2Average),
            SpO2Min = First(rowsByPriority, r => r.SpO2Min),
            SpO2Max = First(rowsByPriority, r => r.SpO2Max),
            VO2Max = First(rowsByPriority, r => r.VO2Max),
            StressScore = First(rowsByPriority, r => r.StressScore),
            BreathingRate = First(rowsByPriority, r => r.BreathingRate),
            Temperature = First(rowsByPriority, r => r.Temperature),
            TemperatureBaseline = First(rowsByPriority, r => r.TemperatureBaseline),
            TemperatureVariation = First(rowsByPriority, r => r.TemperatureVariation),

            HeartRateVariabilityMs = First(rowsByPriority, r => r.HeartRateVariabilityMs),
            OvernightBreathingRate = First(rowsByPriority, r => r.OvernightBreathingRate),

            LightZoneMinutes = First(rowsByPriority, r => r.LightZoneMinutes),
            ModerateZoneMinutes = First(rowsByPriority, r => r.ModerateZoneMinutes),
            VigorousZoneMinutes = First(rowsByPriority, r => r.VigorousZoneMinutes),
            PeakZoneMinutes = First(rowsByPriority, r => r.PeakZoneMinutes),
            ModerateZoneFloorBpm = First(rowsByPriority, r => r.ModerateZoneFloorBpm),

            // Coalesced, not maxed: the stretch and the instant it began are one reading, and
            // taking the longest stretch from one device beside a start time from another would
            // describe a stretch that never happened.
            LongestSedentaryStretchMinutes = First(rowsByPriority, r => r.LongestSedentaryStretchMinutes),
            LongestSedentaryStretchStartUtc = First(rowsByPriority, r => r.LongestSedentaryStretchStartUtc)
        };
    }

    /// <summary>
    /// Orders a member's device connections into the priority the merge uses: the member's primary
    /// device first, then the longest-connected, with Id as a final tiebreak so the ordering is
    /// stable across runs. Matches the ordering WearableSyncWorker applies.
    /// </summary>
    public static IEnumerable<DeviceConnection> ByPriority(IEnumerable<DeviceConnection> connections) =>
        connections
            .OrderByDescending(c => c.IsPrimary)
            .ThenBy(c => c.ConnectedDate ?? DateTime.MaxValue)
            .ThenBy(c => c.Id);

    private static T? First<T>(IReadOnlyList<DeviceActivityLog> rows, Func<DeviceActivityLog, T?> select)
        where T : struct
    {
        foreach (var row in rows)
        {
            var value = select(row);
            if (value.HasValue)
                return value;
        }
        return null;
    }
}
