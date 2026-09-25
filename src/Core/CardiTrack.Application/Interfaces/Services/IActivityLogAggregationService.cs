namespace CardiTrack.Application.Interfaces.Services;

public interface IActivityLogAggregationService
{
    /// <summary>
    /// Recomputes the merged ActivityLog row for one CardiMember-day from every device's raw
    /// DeviceActivityLog row for that day. Idempotent — it always rebuilds from the full raw set,
    /// so running it again, or after a device's row changes, converges on the same result.
    /// Does not save; the caller owns the unit of work.
    /// </summary>
    Task RecomputeAsync(Guid cardiMemberId, DateOnly date);

    /// <summary>
    /// Says what is known about the night that ended on <paramref name="date"/>, on the merged row:
    /// slept, awake with the watch on (written as 0 minutes of sleep), no data, or pending — see
    /// <c>NightSleepClassifier</c>. Reads the member's overnight heart rate, so the caller runs it
    /// after that has been ingested, and outside anything a failed read must not undo. A no-op
    /// when the day has no merged row. Does not save; the caller owns the unit of work.
    /// </summary>
    Task ClassifyNightAsync(Guid cardiMemberId, DateOnly date, CancellationToken ct = default);
}
