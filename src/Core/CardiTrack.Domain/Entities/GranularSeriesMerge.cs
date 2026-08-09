using CardiTrack.Domain.Enums;

namespace CardiTrack.Domain.Entities;

/// <summary>
/// A member's merged minute-grain series over one continuous UTC window — what
/// <c>IGranularMetricRepository.GetWindowAsync</c> returns and what the moving-window inference in
/// docs/llm_design.md consumes. Slot 0 of each series is the minute starting at
/// <see cref="FromUtc"/>; null slots mean no device reported that minute.
/// </summary>
public sealed record GranularWindow
{
    public required Guid CardiMemberId { get; init; }
    public required DateTime FromUtc { get; init; }
    public required DateTime ToUtc { get; init; }

    /// <summary>One fixed-length minute series per metric that had any data in the window.</summary>
    public required IReadOnlyDictionary<GranularMetric, float?[]> MinuteSeries { get; init; }
}

/// <summary>
/// Merges per-device <see cref="GranularMetricHour"/> vectors into one series, minute by minute —
/// the same coalescing rule <see cref="ActivityLogMerge"/> applies to day rows, at minute grain.
/// The first device in priority order with a sample for a minute wins that minute; values are
/// never summed, because two wearables on the same wrist count the same steps. Pure, so the rule
/// is unit-testable without a database.
/// </summary>
public static class GranularSeriesMerge
{
    /// <summary>
    /// Coalesces one hour's per-device vectors. <paramref name="rowsByPriority"/> must be ordered
    /// highest-priority device first (<see cref="ActivityLogMerge.ByPriority"/>).
    /// </summary>
    public static float?[] MergeHour(IReadOnlyList<GranularMetricHour> rowsByPriority)
    {
        var merged = new float?[GranularMetricHour.MinutesPerHour];
        foreach (var row in rowsByPriority)
        {
            var limit = Math.Min(row.Values.Length, merged.Length);
            for (var minute = 0; minute < limit; minute++)
                merged[minute] ??= row.Values[minute];
        }
        return merged;
    }
}
