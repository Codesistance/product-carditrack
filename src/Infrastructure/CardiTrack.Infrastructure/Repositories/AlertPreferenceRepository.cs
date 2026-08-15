using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Entities;
using CardiTrack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CardiTrack.Infrastructure.Repositories;

public class AlertPreferenceRepository : Repository<AlertPreference>, IAlertPreferenceRepository
{
    public AlertPreferenceRepository(CardiTrackDbContext context) : base(context)
    {
    }

    public async Task<AlertPreference?> GetByCardiMemberIdAsync(Guid cardiMemberId, CancellationToken ct = default) =>
        await _dbSet.FirstOrDefaultAsync(p => p.CardiMemberId == cardiMemberId, ct);
}
