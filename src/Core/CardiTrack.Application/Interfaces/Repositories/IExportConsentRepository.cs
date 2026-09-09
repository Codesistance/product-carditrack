using CardiTrack.Domain.Entities;

namespace CardiTrack.Application.Interfaces.Repositories;

public interface IExportConsentRepository : IRepository<ExportConsent>
{
    /// <summary>
    /// One unused-or-used consent, scoped to the owner. Ownership is in the
    /// query so a stolen token for another user reads as "not found".
    /// Tracked, because consume updates the same row.
    /// </summary>
    Task<ExportConsent?> GetForOwnerAsync(Guid consentId, Guid ownerUserId, CancellationToken ct = default);

    /// <summary>
    /// Atomically stamps consume when the row is still unused and unexpired.
    /// Returns false if another caller won or the row no longer qualifies.
    /// </summary>
    Task<bool> TryConsumeAsync(
        Guid consentId, Guid ownerUserId, Guid reportId, DateTime utcNow, CancellationToken ct = default);
}
