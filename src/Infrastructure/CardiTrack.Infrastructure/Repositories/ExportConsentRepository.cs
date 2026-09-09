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
}
