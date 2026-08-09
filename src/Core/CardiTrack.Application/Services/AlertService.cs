using CardiTrack.Application.DTOs.Common;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Domain.Extensions;

namespace CardiTrack.Application.Services;

/// <inheritdoc cref="IAlertService"/>
public class AlertService : IAlertService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICardiMemberAccessService _access;

    public AlertService(IUnitOfWork unitOfWork, ICardiMemberAccessService access)
    {
        _unitOfWork = unitOfWork;
        _access = access;
    }

    public async Task<AlertListResponse> GetAlertsAsync(
        Guid requestingUserId,
        Guid? cardiMemberId = null,
        AlertSeverity? severity = null,
        AlertStatus? status = null,
        DateTime? from = null,
        DateTime? to = null,
        int limit = AlertQuery.DefaultLimit,
        int offset = 0,
        CancellationToken ct = default)
    {
        IReadOnlyCollection<Guid> scope;
        if (cardiMemberId is { } id)
        {
            // Throws when the member isn't readable, so an unauthorised id is indistinguishable
            // from a non-existent one — same non-disclosure rule the dashboard follows.
            await _access.RequireViewAccessAsync(requestingUserId, id, ct);
            scope = [id];
        }
        else
        {
            scope = await _access.GetViewableMemberIdsAsync(requestingUserId, ct);
        }

        var query = new AlertQuery(
            scope,
            severity,
            status,
            ToUtc(from),
            ToUtc(to),
            Math.Clamp(limit, 1, AlertQuery.MaxLimit),
            Math.Max(offset, 0));

        var alerts = await _unitOfWork.Alerts.QueryAsync(query, ct);
        var total = await _unitOfWork.Alerts.CountAsync(query, ct);
        var unread = await _unitOfWork.Alerts.CountUnreadAsync(scope, ct);

        var members = await LoadMembersAsync(alerts);

        return new AlertListResponse
        {
            Alerts = alerts.Select(a => ToSummary(a, members.GetValueOrDefault(a.CardiMemberId))).ToList(),
            Total = total,
            UnreadCount = unread,
        };
    }

    public async Task<AlertAcknowledgementResponse> AcknowledgeAsync(
        Guid requestingUserId, Guid alertId, CancellationToken ct = default)
    {
        var alert = await _unitOfWork.Alerts.GetByIdWithCardiMemberAsync(alertId);
        if (alert is null || !alert.IsActive)
            throw new KeyNotFoundException("Alert not found");

        await _access.RequireViewAccessAsync(requestingUserId, alert.CardiMemberId, ct);

        // Idempotent on purpose: two family members tapping "handled" seconds apart is the
        // expected case, and the second tap must not overwrite who actually dealt with it.
        if (alert.AcknowledgedDate is null)
        {
            alert.AcknowledgedDate = DateTime.UtcNow;
            alert.AcknowledgedByUserId = requestingUserId;
            _unitOfWork.Alerts.Update(alert);
            await _unitOfWork.SaveChangesAsync();
        }

        var unread = await _unitOfWork.Alerts.CountUnreadAsync(
            await _access.GetViewableMemberIdsAsync(requestingUserId, ct), ct);

        return new AlertAcknowledgementResponse
        {
            AlertId = alert.Id,
            Status = StatusLabel(alert),
            AcknowledgedAt = alert.AcknowledgedDate,
            AcknowledgedByUserId = alert.AcknowledgedByUserId,
            UnreadCount = unread,
        };
    }

    /// <summary>
    /// The date filters as PostgreSQL will accept them. <c>TriggeredDate</c> is a
    /// <c>timestamp with time zone</c> and the host disables
    /// <c>Npgsql.EnableLegacyTimestampBehavior</c>, so Npgsql throws on any <see cref="DateTime"/>
    /// whose <see cref="DateTime.Kind"/> is not UTC — and the mobile "Today"/"This Week" chips
    /// send local midnight. An unspecified kind is read as UTC, the usual reading of a bare
    /// timestamp on the wire.
    /// </summary>
    private static DateTime? ToUtc(DateTime? value) => value is not { } instant
        ? null
        : instant.Kind switch
        {
            DateTimeKind.Utc => instant,
            DateTimeKind.Local => instant.ToUniversalTime(),
            _ => DateTime.SpecifyKind(instant, DateTimeKind.Utc),
        };

    /// <summary>
    /// The members named by this page of alerts, keyed by id — in one read rather than one per
    /// member, since every id is known up front.
    /// </summary>
    private async Task<Dictionary<Guid, CardiMember>> LoadMembersAsync(IReadOnlyList<Alert> alerts)
    {
        var memberIds = alerts.Select(a => a.CardiMemberId).Distinct().ToList();
        if (memberIds.Count == 0)
            return [];

        var members = await _unitOfWork.CardiMembers.FindAsync(m => memberIds.Contains(m.Id));
        return members.ToDictionary(m => m.Id);
    }

    private static AlertSummaryResponse ToSummary(Alert alert, CardiMember? member) => new()
    {
        AlertId = alert.Id,
        CardiMemberId = alert.CardiMemberId,
        CardiMemberName = member?.Name ?? string.Empty,
        // No photo storage exists yet (see CardiMemberService) — the field is here so the card
        // can show one the moment it does, and falls back to initials until then.
        CardiMemberPhotoUrl = null,
        EmergencyContactPhone = member?.EmergencyContactPhone,
        EmergencyContactName = member?.EmergencyContactName,
        Type = alert.AlertType.GetDisplayName(),
        Severity = SeverityLabel(alert.Severity),
        Status = StatusLabel(alert),
        Title = alert.Title,
        Message = alert.Message,
        TriggeredAt = alert.TriggeredDate,
        AcknowledgedAt = alert.AcknowledgedDate,
        AcknowledgedByUserId = alert.AcknowledgedByUserId,
    };

    private static string SeverityLabel(AlertSeverity severity) =>
        severity.ToString().ToLowerInvariant();

    private static string StatusLabel(Alert alert) => ToStatus(alert).ToString().ToLowerInvariant();

    /// <summary>The lifecycle rule, in one place — see <see cref="AlertStatus"/>.</summary>
    private static AlertStatus ToStatus(Alert alert) =>
        alert.IsResolved ? AlertStatus.Resolved
        : alert.AcknowledgedDate is not null ? AlertStatus.Acknowledged
        : AlertStatus.New;
}
