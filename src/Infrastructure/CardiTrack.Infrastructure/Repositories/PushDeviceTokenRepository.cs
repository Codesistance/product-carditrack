using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CardiTrack.Infrastructure.Repositories;

public class PushDeviceTokenRepository : Repository<PushDeviceToken>, IPushDeviceTokenRepository
{
    public PushDeviceTokenRepository(CardiTrackDbContext context) : base(context)
    {
    }

    public async Task<PushDeviceToken?> GetByFingerprintAsync(string tokenFingerprint, CancellationToken ct = default) =>
        await _dbSet.FirstOrDefaultAsync(t => t.TokenFingerprint == tokenFingerprint, ct);

    public async Task<PushDeviceToken?> GetByUserAndDeviceAsync(Guid userId, string deviceId, CancellationToken ct = default) =>
        await _dbSet.FirstOrDefaultAsync(t => t.UserId == userId && t.DeviceId == deviceId, ct);

    public async Task<IReadOnlyList<PushDeviceToken>> GetLiveForUserAsync(
        Guid userId, DeliveryCategory category, CancellationToken ct = default) =>
        await _dbSet
            .Where(t => t.UserId == userId
                        && t.DisabledDate == null
                        && (t.OsAuthorizationStatus == OsAuthorizationStatus.Granted
                            || t.OsAuthorizationStatus == OsAuthorizationStatus.Provisional)
                        && (category != DeliveryCategory.Safety || t.SafetyChannelEnabled))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<PushDeviceToken>> GetDueForLivenessProbeAsync(
        DateTime utcNow, CancellationToken ct = default)
    {
        var today = utcNow.Date;
        return await _dbSet
            .Where(t => t.DisabledDate == null && (t.LastAckDate == null || t.LastAckDate.Value.Date < today))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<PushDeviceToken>> GetDueForHardDeleteAsync(
        DateTime utcNow, CancellationToken ct = default)
    {
        var cutoff = utcNow.AddDays(-30);
        return await _dbSet
            .Where(t => t.DisabledDate != null && t.DisabledDate.Value <= cutoff)
            .ToListAsync(ct);
    }
}
