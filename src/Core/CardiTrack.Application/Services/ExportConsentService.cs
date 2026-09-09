using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Exceptions;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Reports;
using CardiTrack.Domain.Entities;

namespace CardiTrack.Application.Services;

/// <summary>
/// Mints and consumes the single-use token that must accompany every export.
/// </summary>
public class ExportConsentService : IExportConsentService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICardiMemberAccessService _access;

    public ExportConsentService(IUnitOfWork unitOfWork, ICardiMemberAccessService access)
    {
        _unitOfWork = unitOfWork;
        _access = access;
    }

    public async Task<ExportConsentResponse> RecordAsync(
        Guid requestingUserId, RecordExportConsentRequest request, CancellationToken ct = default)
    {
        if (!request.AcceptedResponsibility)
            throw new ExportConsentException("Confirm you accept responsibility before exporting.");

        await _access.RequireViewAccessAsync(requestingUserId, request.CardiMemberIds, ct);

        var now = DateTime.UtcNow;
        var snapshot = ToGenerateRequest(request);
        var consent = new ExportConsent
        {
            OwnerUserId = requestingUserId,
            CardiMemberIds = request.CardiMemberIds.ToList(),
            DateRangeFrom = request.DateRangeFrom,
            DateRangeTo = request.DateRangeTo,
            Format = request.Format,
            IncludeMetrics = request.IncludeMetrics,
            IncludeTrends = request.IncludeTrends,
            IncludeAlerts = request.IncludeAlerts,
            IncludeJournals = request.IncludeJournals,
            IncludeNotices = request.IncludeNotices,
            IncludeDevices = request.IncludeDevices,
            JournalEntryDate = request.JournalEntryDate,
            JournalAudience = request.JournalAudience,
            PolicyVersion = ExportConsentPolicy.Version,
            PolicySha256 = ExportConsentPolicy.Sha256Hex,
            RequestFingerprint = ExportConsentPolicy.Fingerprint(snapshot),
            Method = request.Method,
            ExpiresAt = now.Add(ExportConsentPolicy.Lifetime)
        };

        await _unitOfWork.ExportConsents.AddAsync(consent);
        await _unitOfWork.SaveChangesAsync();

        return new ExportConsentResponse
        {
            ConsentToken = consent.Id.ToString("N"),
            ExpiresAt = new DateTimeOffset(DateTime.SpecifyKind(consent.ExpiresAt, DateTimeKind.Utc))
        };
    }

    public async Task ConsumeAsync(
        Guid requestingUserId,
        string consentToken,
        GenerateReportRequest request,
        Guid reportId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(consentToken) || !Guid.TryParse(consentToken, out var id))
            throw new ExportConsentException("Confirm you accept responsibility before exporting.");

        var consent = await _unitOfWork.ExportConsents.GetForOwnerAsync(id, requestingUserId, ct);
        var now = DateTime.UtcNow;

        if (consent is null || consent.ExpiresAt <= now)
            throw new ExportConsentException("That confirmation expired — please confirm again.");

        if (consent.ConsumedAt is not null)
            throw new ExportConsentException("That confirmation was already used — please confirm again.");

        if (!string.Equals(
                consent.RequestFingerprint,
                ExportConsentPolicy.Fingerprint(request),
                StringComparison.Ordinal))
        {
            throw new ExportConsentException(
                "The export no longer matches what you confirmed. Please confirm again.");
        }

        consent.ConsumedAt = now;
        consent.ReportId = reportId;
        _unitOfWork.ExportConsents.Update(consent);
        await _unitOfWork.SaveChangesAsync();
    }

    private static GenerateReportRequest ToGenerateRequest(RecordExportConsentRequest request) => new()
    {
        CardiMemberIds = request.CardiMemberIds,
        DateRangeFrom = request.DateRangeFrom,
        DateRangeTo = request.DateRangeTo,
        Format = request.Format,
        IncludeMetrics = request.IncludeMetrics,
        IncludeTrends = request.IncludeTrends,
        IncludeAlerts = request.IncludeAlerts,
        IncludeJournals = request.IncludeJournals,
        IncludeNotices = request.IncludeNotices,
        IncludeDevices = request.IncludeDevices,
        JournalEntryDate = request.JournalEntryDate,
        JournalAudience = request.JournalAudience
    };
}
