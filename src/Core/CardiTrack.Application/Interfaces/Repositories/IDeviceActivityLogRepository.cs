using CardiTrack.Domain.Entities;

namespace CardiTrack.Application.Interfaces.Repositories;

public interface IDeviceActivityLogRepository : IRepository<DeviceActivityLog>
{
    /// <summary>
    /// Writes one row per device per day, keyed on (DeviceConnectionId, Date). A re-fetch that
    /// carries the same values leaves the row — and its UpdatedDate — untouched, so UpdatedDate
    /// marks when the provider's data last changed rather than when it was last polled.
    /// </summary>
    Task UpsertAsync(DeviceActivityLog log);

    /// <summary>Every device's raw row for one member-day — the input to the merge.</summary>
    Task<IEnumerable<DeviceActivityLog>> GetByCardiMemberAndDateAsync(Guid cardiMemberId, DateOnly date);
}
