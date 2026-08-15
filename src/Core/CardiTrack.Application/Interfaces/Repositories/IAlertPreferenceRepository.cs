using CardiTrack.Domain.Entities;

namespace CardiTrack.Application.Interfaces.Repositories;

public interface IAlertPreferenceRepository : IRepository<AlertPreference>
{
    Task<AlertPreference?> GetByCardiMemberIdAsync(Guid cardiMemberId, CancellationToken ct = default);
}
