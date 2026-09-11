using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;

namespace CardiTrack.Application.Interfaces.Services;

public interface IExportConsentService
{
    /// <summary>
    /// Records a responsibility confirmation and returns a short-lived token.
    /// Throws <see cref="KeyNotFoundException"/> unless the caller may view
    /// every named CardiMember.
    /// </summary>
    Task<ExportConsentResponse> RecordAsync(
        Guid requestingUserId, RecordExportConsentRequest request, CancellationToken ct = default);

    /// <summary>
    /// Mints a two-minute token from an in-force standing grant for this
    /// snapshot. Throws <see cref="KeyNotFoundException"/> when there is no
    /// grant to reuse, or the caller may not view a named CardiMember.
    /// </summary>
    Task<ExportConsentResponse> ReuseAsync(
        Guid requestingUserId, GenerateReportRequest request, CancellationToken ct = default);

    /// <summary>
    /// Marks the token used and binds it to <paramref name="reportId"/>.
    /// Throws <see cref="Exceptions.ExportConsentException"/> when the token is
    /// missing, expired, already used, revoked, or does not match <paramref name="request"/>.
    /// </summary>
    Task ConsumeAsync(
        Guid requestingUserId,
        string consentToken,
        GenerateReportRequest request,
        Guid reportId,
        CancellationToken ct = default);

    /// <summary>Every confirmation this caregiver has given, newest first.</summary>
    Task<IReadOnlyList<ExportConsentHistoryItem>> ListAsync(
        Guid requestingUserId, CancellationToken ct = default);

    /// <summary>
    /// Stops a standing grant. Later exports must confirm again. Throws
    /// <see cref="KeyNotFoundException"/> when the id is unknown, not theirs,
    /// or is not a standing grant they can stop.
    /// </summary>
    Task RevokeAsync(Guid requestingUserId, Guid consentId, CancellationToken ct = default);
}
