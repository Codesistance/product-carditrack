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
    /// The newest standing grant still in force under the current policy text.
    /// Untracked — reuse mints a child row rather than mutating this one.
    /// </summary>
    Task<ExportConsent?> GetActiveStandingAsync(
        Guid ownerUserId, DateTime utcNow, string policySha256, CancellationToken ct = default);

    /// <summary>Every confirmation this owner has given, newest first.</summary>
    Task<IReadOnlyList<ExportConsent>> ListForOwnerAsync(
        Guid ownerUserId, CancellationToken ct = default);

    /// <summary>
    /// Atomically stamps consume when the row is still unused and unexpired.
    /// Returns false if another caller won or the row no longer qualifies.
    /// </summary>
    Task<bool> TryConsumeAsync(
        Guid consentId, Guid ownerUserId, Guid reportId, DateTime utcNow, CancellationToken ct = default);

    /// <summary>
    /// Stops one standing grant. Returns false when the row is missing, not
    /// theirs, already revoked, or was never a standing grant.
    /// </summary>
    Task<bool> TryRevokeAsync(
        Guid consentId, Guid ownerUserId, DateTime utcNow, CancellationToken ct = default);

    /// <summary>
    /// Stops every in-force standing grant for this owner. Used when they
    /// record a new remembered confirmation so only one grant is live.
    /// </summary>
    Task RevokeActiveStandingAsync(Guid ownerUserId, DateTime utcNow, CancellationToken ct = default);
}
