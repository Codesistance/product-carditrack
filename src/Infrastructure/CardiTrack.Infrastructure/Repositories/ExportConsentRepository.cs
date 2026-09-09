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
}
