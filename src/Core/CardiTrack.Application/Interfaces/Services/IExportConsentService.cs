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
    /// Marks the token used and binds it to <paramref name="reportId"/>.
    /// Throws <see cref="Exceptions.ExportConsentException"/> when the token is
    /// missing, expired, already used, or does not match <paramref name="request"/>.
    /// </summary>
    Task ConsumeAsync(
        Guid requestingUserId,
        string consentToken,
        GenerateReportRequest request,
        Guid reportId,
        CancellationToken ct = default);
}
