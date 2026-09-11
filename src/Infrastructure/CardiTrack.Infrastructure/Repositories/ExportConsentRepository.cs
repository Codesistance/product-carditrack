using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Entities;
using CardiTrack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CardiTrack.Infrastructure.Repositories;

public class ExportConsentRepository : Repository<ExportConsent>, IExportConsentRepository
{
    public ExportConsentRepository(CardiTrackDbContext context) : base(context)
    {
    }

    public Task<ExportConsent?> GetForOwnerAsync(
        Guid consentId, Guid ownerUserId, CancellationToken ct = default) =>
        _dbSet.FirstOrDefaultAsync(c => c.Id == consentId && c.OwnerUserId == ownerUserId, ct);

    public Task<ExportConsent?> GetActiveStandingAsync(
        Guid ownerUserId, DateTime utcNow, string policySha256, CancellationToken ct = default) =>
        _dbSet.AsNoTracking()
            .Where(c =>
                c.OwnerUserId == ownerUserId
                && c.ReusedFromConsentId == null
                && c.RevokedAt == null
                && c.RememberUntil != null
                && c.RememberUntil > utcNow
                && c.PolicySha256 == policySha256)
            .OrderByDescending(c => c.CreatedDate)
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<ExportConsent>> ListForOwnerAsync(
        Guid ownerUserId, CancellationToken ct = default) =>
        await _dbSet.AsNoTracking()
            .Where(c => c.OwnerUserId == ownerUserId)
            .OrderByDescending(c => c.CreatedDate)
            .ToListAsync(ct);

    public async Task<bool> TryConsumeAsync(
        Guid consentId, Guid ownerUserId, Guid reportId, DateTime utcNow, CancellationToken ct = default)
    {
        var updated = await _dbSet
            .Where(c =>
                c.Id == consentId
                && c.OwnerUserId == ownerUserId
                && c.ConsumedAt == null
                && c.ExpiresAt > utcNow)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.ConsumedAt, utcNow)
                .SetProperty(c => c.ReportId, reportId)
                .SetProperty(c => c.UpdatedDate, utcNow), ct);

        return updated == 1;
    }

    public async Task<bool> TryRevokeAsync(
        Guid consentId, Guid ownerUserId, DateTime utcNow, CancellationToken ct = default)
    {
        var updated = await _dbSet
            .Where(c =>
                c.Id == consentId
                && c.OwnerUserId == ownerUserId
                && c.ReusedFromConsentId == null
                && c.RevokedAt == null
                && c.RememberUntil != null
                && c.RememberUntil > utcNow)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.RevokedAt, utcNow)
                .SetProperty(c => c.UpdatedDate, utcNow), ct);

        return updated == 1;
    }

    public Task RevokeActiveStandingAsync(
        Guid ownerUserId, DateTime utcNow, CancellationToken ct = default) =>
        _dbSet
            .Where(c =>
                c.OwnerUserId == ownerUserId
                && c.ReusedFromConsentId == null
                && c.RevokedAt == null
                && c.RememberUntil != null
                && c.RememberUntil > utcNow)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.RevokedAt, utcNow)
                .SetProperty(c => c.UpdatedDate, utcNow), ct);
}