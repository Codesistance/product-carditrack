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
}
