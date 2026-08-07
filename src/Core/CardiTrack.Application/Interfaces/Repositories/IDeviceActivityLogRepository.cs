using CardiTrack.Domain.Entities;

namespace CardiTrack.Application.Interfaces.Repositories;

public interface IDeviceActivityLogRepository : IRepository<DeviceActivityLog>
{
    /// <summary>Writes one row per device per day, keyed on (DeviceConnectionId, Date).</summary>
    Task UpsertAsync(DeviceActivityLog log);

    /// <summary>Every device's raw row for one member-day — the input to the merge.</summary>
    Task<IEnumerable<DeviceActivityLog>> GetByCardiMemberAndDateAsync(Guid cardiMemberId, DateOnly date);
}
