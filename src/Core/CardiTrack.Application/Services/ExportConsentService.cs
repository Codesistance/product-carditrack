using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Exceptions;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Reports;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Services;

/// <summary>
/// Mints and consumes the single-use token that must accompany every export,
/// and the standing grant later exports may reuse.
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
        var rememberFor = Enum.IsDefined(request.RememberFor)
            ? request.RememberFor
            : ExportConsentRememberFor.ThisExport;
        var rememberForDuration = ExportConsentPolicy.RememberDuration(rememberFor);
        var rememberUntil = rememberForDuration is { } duration ? now.Add(duration) : (DateTime?)null;

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
            RememberFor = rememberFor,
            RememberUntil = rememberUntil,
            ExpiresAt = now.Add(ExportConsentPolicy.Lifetime)
        };

        if (rememberUntil is not null)
        {
            await _unitOfWork.BeginTransactionAsync();
            try
            {
                await _unitOfWork.ExportConsents.RevokeActiveStandingAsync(requestingUserId, now, ct);
                await _unitOfWork.ExportConsents.AddAsync(consent);
                await _unitOfWork.SaveChangesAsync();
                await _unitOfWork.CommitTransactionAsync();
            }
            catch
            {
                await _unitOfWork.RollbackTransactionAsync();
                throw;
            }
        }
        else
        {
            await _unitOfWork.ExportConsents.AddAsync(consent);
            await _unitOfWork.SaveChangesAsync();
        }

        return ToRecordedResponse(consent);
    }

    public async Task<ExportConsentResponse> ReuseAsync(
        Guid requestingUserId,
        Guid standingConsentId,
        GenerateReportRequest request,
        CancellationToken ct = default)
    {
        await _access.RequireViewAccessAsync(requestingUserId, request.CardiMemberIds, ct);

        var now = DateTime.UtcNow;
        var grant = await _unitOfWork.ExportConsents.GetForOwnerAsync(standingConsentId, requestingUserId, ct);
        if (grant is null || !IsStandingGrantReusable(grant, now))
            throw new KeyNotFoundException("You don't have a confirmation we can reuse — please confirm again.");

        var child = new ExportConsent
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
            PolicyVersion = grant.PolicyVersion,
            PolicySha256 = grant.PolicySha256,
            RequestFingerprint = ExportConsentPolicy.Fingerprint(request),
            Method = grant.Method,
            RememberFor = ExportConsentRememberFor.ThisExport,
            ReusedFromConsentId = grant.Id,
            ExpiresAt = now.Add(ExportConsentPolicy.Lifetime)
        };

        await _unitOfWork.ExportConsents.AddAsync(child);
        await _unitOfWork.SaveChangesAsync();

        return ToReusedResponse(child, grant);
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

        if (consent.RevokedAt is not null)
            throw new ExportConsentException("That confirmation is no longer in force — please confirm again.");

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

        if (consent.ReusedFromConsentId is { } grantId)
        {
            var grant = await _unitOfWork.ExportConsents.GetForOwnerAsync(grantId, requestingUserId, ct);
            if (!IsStandingGrantUnrevoked(grant, now))
            {
                throw new ExportConsentException(
                    "That confirmation is no longer in force — please confirm again.");
            }
        }

        if (!await _unitOfWork.ExportConsents.TryConsumeAsync(id, requestingUserId, reportId, now, ct))
            throw new ExportConsentException("That confirmation was already used — please confirm again.");
    }

    public async Task<IReadOnlyList<ExportConsentHistoryItem>> ListAsync(
        Guid requestingUserId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var rows = await _unitOfWork.ExportConsents.ListForOwnerAsync(requestingUserId, ct);
        return [.. rows.Select(c => ToHistoryItem(c, now))];
    }

    public async Task RevokeAsync(Guid requestingUserId, Guid consentId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        if (!await _unitOfWork.ExportConsents.TryRevokeAsync(consentId, requestingUserId, now, ct))
            throw new KeyNotFoundException("We couldn't find that confirmation.");
    }

    private static bool IsStandingGrantReusable(ExportConsent grant, DateTime utcNow) =>
        IsStandingGrantUnrevoked(grant, utcNow)
        && string.Equals(grant.PolicySha256, ExportConsentPolicy.Sha256Hex, StringComparison.Ordinal);

    private static bool IsStandingGrantUnrevoked(ExportConsent? grant, DateTime utcNow) =>
        grant is not null
        && grant.ReusedFromConsentId is null
        && grant.RevokedAt is null
        && grant.RememberUntil is { } until
        && until > utcNow;

    private static ExportConsentResponse ToRecordedResponse(ExportConsent consent) => new()
    {
        ConsentToken = consent.Id.ToString("N"),
        ExpiresAt = Utc(consent.ExpiresAt),
        RememberUntil = consent.RememberUntil is { } until ? Utc(until) : null
    };

    private static ExportConsentResponse ToReusedResponse(ExportConsent child, ExportConsent grant)
    {
        var rememberUntil = grant.RememberUntil is { } until ? Utc(until) : Utc(grant.ExpiresAt);
        return new ExportConsentResponse
        {
            ConsentToken = child.Id.ToString("N"),
            ExpiresAt = Utc(child.ExpiresAt),
            Reused = true,
            ReusedFromConsentId = grant.Id,
            OriginalConsentedAt = Utc(grant.CreatedDate),
            RememberUntil = rememberUntil,
            ReuseNotice = ExportConsentPolicy.ReuseNotice(
                FormatDay(grant.CreatedDate),
                FormatDay(grant.RememberUntil ?? grant.ExpiresAt))
        };
    }

    private static ExportConsentHistoryItem ToHistoryItem(ExportConsent consent, DateTime utcNow)
    {
        var reused = consent.ReusedFromConsentId is not null;
        var canRevoke = !reused
            && consent.RevokedAt is null
            && consent.RememberUntil is { } until
            && until > utcNow;

        return new ExportConsentHistoryItem
        {
            Id = consent.Id,
            RecordedAt = Utc(consent.CreatedDate),
            Method = consent.Method,
            RememberFor = consent.RememberFor,
            RememberUntil = consent.RememberUntil is { } rememberUntil ? Utc(rememberUntil) : null,
            RevokedAt = consent.RevokedAt is { } revoked ? Utc(revoked) : null,
            ConsumedAt = consent.ConsumedAt is { } consumed ? Utc(consumed) : null,
            Reused = reused,
            CanRevoke = canRevoke,
            CanReuse = canRevoke
                && string.Equals(consent.PolicySha256, ExportConsentPolicy.Sha256Hex, StringComparison.Ordinal),
            Summary = HistorySummary(consent, reused, canRevoke)
        };
    }

    private static string HistorySummary(ExportConsent consent, bool reused, bool canRevoke)
    {
        var proof = consent.Method == ExportConsentMethod.Biometric
            ? "fingerprint or face unlock"
            : "password";

        if (reused)
            return $"Reused an earlier confirmation · {proof}";

        if (consent.RevokedAt is { } revoked)
            return $"Stopped on {FormatDay(revoked)} · {proof}";

        if (canRevoke && consent.RememberUntil is { } until)
            return $"In force until {FormatDay(until)} · {proof}";

        if (consent.RememberUntil is { } remembered)
            return $"Kept until {FormatDay(remembered)} · {proof}";

        return $"Just this export · {proof}";
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

    private static DateTimeOffset Utc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static string FormatDay(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture);
}
