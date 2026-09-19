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
                        && (category != DeliveryCategory.Safety || t.SafetyChannelEnabled)
                        // Nothing leaves the server for an account that has asked to be deleted.
                        // The enqueue path already declines to create a delivery for one
                        // (DispatchService.EnqueueAsync); this is the other half — deliveries
                        // queued *before* the request, and the retries and escalations that
                        // follow them. It cannot be left to the token being disabled when the
                        // request is recorded, because signing in to cancel re-registers the
                        // device on the next launch, which would make those queued pushes
                        // deliverable again while the request still stands. Written the same way
                        // DeviceConnectionRepository writes it at its sync-scheduling sites.
                        && !_context.Users.Any(u => u.Id == t.UserId && u.DeletionRequestedAtUtc != null))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<PushDeviceToken>> GetDueForLivenessProbeAsync(
        DateTime utcNow, CancellationToken ct = default)
    {
        // Compare against the day boundary directly rather than truncating LastAckDate
        // (.Value.Date translates to date_trunc per row, which defeats any index on the
        // column). "truncated day < today" and "timestamp < start of today" select the
        // same rows when today is a UTC day boundary.
        var today = utcNow.Date;
        return await _dbSet
            .Where(t => t.DisabledDate == null
                        && (t.LastAckDate == null || t.LastAckDate < today)
                        // A silent probe is still a push that reaches the handset, and an
                        // account on its way out has nothing to prove about its reachability.
                        && !_context.Users.Any(u => u.Id == t.UserId && u.DeletionRequestedAtUtc != null))
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
