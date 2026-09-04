using CardiTrack.Application.DTOs.Common;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Exceptions;
using CardiTrack.Application.Interfaces.Clients;
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
    private readonly IProfilePhotoStorage _photoStorage;
    private readonly TimeProvider _timeProvider;

    /// <param name="timeProvider">
    /// The clock the elapsed match is anchored to. Injectable because which windows
    /// <see cref="GetByIdAsync"/> fetches depends on the hour it is called in — before the first
    /// whole local hour of the day there is no elapsed stretch to compare, so a test that cannot
    /// pin the clock asserts a different thing when it runs just after midnight.
    /// </param>
    public AlertService(
        IUnitOfWork unitOfWork,
        ICardiMemberAccessService access,
        IProfilePhotoStorage photoStorage,
        TimeProvider? timeProvider = null)
    {
        _unitOfWork = unitOfWork;
        _access = access;
        _photoStorage = photoStorage;
        _timeProvider = timeProvider ?? TimeProvider.System;
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
        var photoUrls = await LoadPhotoUrlsAsync(members, ct);

        return new AlertListResponse
        {
            Alerts = alerts
                .Select(a => ToSummary(
                    a,
                    members.GetValueOrDefault(a.CardiMemberId),
                    photoUrls.GetValueOrDefault(a.CardiMemberId)))
                .ToList(),
            Total = total,
            UnreadCount = unread,
        };
    }

    public async Task<AlertDetailResponse> GetByIdAsync(
        Guid requestingUserId, Guid alertId, CancellationToken ct = default)
    {
        var alert = await _unitOfWork.Alerts.GetByIdWithCardiMemberAsync(alertId);
        // Same anti-enumeration as HealthInsightService.AnalyzeAlertAsync: missing, inactive,
        // and unreadable ids all report "Alert not found", so a guessed id cannot be probed.
        if (alert is null
            || !alert.IsActive
            || !await _access.HasViewAccessAsync(requestingUserId, alert.CardiMemberId, ct))
            throw new KeyNotFoundException("Alert not found");

        var member = await _unitOfWork.CardiMembers.GetByIdAsync(alert.CardiMemberId);

        User? acknowledger = null;
        if (alert.AcknowledgedByUserId is { } userId)
            acknowledger = await _unitOfWork.Users.GetByIdAsync(userId);

        var rule = AlertDetailComposer.ReadRule(alert.MetricValues);
        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;

        // Fetch only the window the chart will plot. A sleep alert must not pay for six
        // dashboard metrics, and device-silence has no health series at all.
        IReadOnlyList<ActivityLog> logs = [];
        var days = AlertDetailComposer.DailyLogDays(rule);
        var today = DateOnly.FromDateTime(utcNow);
        var utcTriggered = ToUtc(alert.TriggeredDate);
        var firedOn = DateOnly.FromDateTime(utcTriggered);
        ElapsedMatch? elapsed = null;

        if (days > 0)
        {
            // The member's anchor clock, not UTC — the same one StatisticalAlertService evaluated
            // the rule on. Reading an alert back on a different calendar than the one that raised
            // it is how a caregiver ends up looking at a window whose "yesterday" is not the
            // yesterday the alert is about. Resolved only when a daily window is actually being
            // plotted: it costs a links query plus a user read, and device-silence and the
            // realtime-HR rule never look at a calendar day.
            var zone = await MemberAnchorTimeZone.ResolveAsync(_unitOfWork, alert.CardiMemberId);
            elapsed = AlertDetailComposer.ElapsedMatchFor(utcNow, zone);
            today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utcNow, zone));
            firedOn = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utcTriggered, zone));

            logs = (await _unitOfWork.ActivityLogs.GetByCardiMemberAndDateRangeAsync(
                    alert.CardiMemberId, today.AddDays(-(days - 1)), today))
                .ToList();
        }

        GranularWindow? granular = null;
        if (AlertDetailComposer.NeedsGranular(rule)
            && AlertDetailComposer.GranularBounds(alert.MetricValues) is { } bounds)
        {
            granular = await _unitOfWork.GranularMetrics.GetWindowAsync(
                alert.CardiMemberId, bounds.FromUtc, bounds.ToUtc, ct);
        }

        PatternBaseline? baseline = null;
        if (days > 0 || granular is not null)
        {
            baseline = await _unitOfWork.PatternBaselines.GetLatestByCardiMemberAsync(
                alert.CardiMemberId, BaselineProgress.PeriodDays);
        }

        var elapsedSteps = AlertDetailComposer.NeedsElapsedMatch(rule) && elapsed is { } match
            ? await ElapsedStepsAsync(alert.CardiMemberId, match, ct)
            : null;

        var photoUrl = member?.PhotoObjectName is { } photoObjectName
            ? await _photoStorage.GetReadUrlAsync(photoObjectName, ct)
            : null;

        return AlertDetailComposer.Compose(
            alert, member, acknowledger, logs, today, granular, baseline, elapsedSteps, firedOn,
            photoUrl);
    }

    /// <summary>
    /// Today-so-far and the same run of hours yesterday, read off the hourly rollup ladder, so the
    /// day in progress can be reported against something it is actually comparable to.
    /// </summary>
    /// <remarks>
    /// Rollups rather than minute vectors: the two stretches together reach ~48 hours by late
    /// evening, <c>GetWindowAsync</c> returns every metric's full minute grid for what it is
    /// asked, and the detail page re-polls this endpoint the whole time it is open. Steps per hour
    /// is precisely what <c>MetricRollupsHourly</c> is for. Two fetches rather than one spanning
    /// both days, because the stretch between them — yesterday evening and last night — is hours
    /// neither figure counts.
    /// </remarks>
    private async Task<ElapsedSteps?> ElapsedStepsAsync(
        Guid cardiMemberId, ElapsedMatch match, CancellationToken ct)
    {
        var todayHours = await _unitOfWork.GranularMetrics.GetRollupsAsync(
            cardiMemberId, GranularMetric.Steps, match.TodayFromUtc, match.TodayToUtc, ct);
        var comparisonHours = await _unitOfWork.GranularMetrics.GetRollupsAsync(
            cardiMemberId, GranularMetric.Steps,
            match.ComparisonFromUtc, match.ComparisonToUtc, ct);

        return AlertDetailComposer.ElapsedStepsFrom(todayHours, comparisonHours, match.Hours);
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
            alert.AcknowledgedDate = _timeProvider.GetUtcNow().UtcDateTime;
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

    public async Task<AlertAcknowledgementResponse> UnacknowledgeAsync(
        Guid requestingUserId, Guid alertId, CancellationToken ct = default)
    {
        var alert = await _unitOfWork.Alerts.GetByIdWithCardiMemberAsync(alertId);
        if (alert is null || !alert.IsActive)
            throw new KeyNotFoundException("Alert not found");

        // Same bar as acknowledging, not the higher one Delete asks for: this restores an alert
        // to everyone's attention rather than taking it away, so the cautious direction here is
        // to allow it.
        await _access.RequireViewAccessAsync(requestingUserId, alert.CardiMemberId, ct);

        if (alert.IsResolved)
        {
            throw new AlertStateException(
                "This alert has already been resolved, so it can't be marked unhandled again.");
        }

        // Idempotent, mirroring AcknowledgeAsync — two family members undoing at once is the same
        // expected race, and the second one must not fail.
        if (alert.AcknowledgedDate is not null)
        {
            alert.AcknowledgedDate = null;
            alert.AcknowledgedByUserId = null;
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

    public async Task DeleteAsync(Guid requestingUserId, Guid alertId, CancellationToken ct = default)
    {
        var alert = await _unitOfWork.Alerts.GetByIdWithCardiMemberAsync(alertId);
        if (alert is null || !alert.IsActive)
            throw new KeyNotFoundException("Alert not found");

        // Manage, not view: removing an alert is a more consequential action than reading one,
        // same bar CardiMemberService.RemoveAsync applies to member removal. Soft-delete only
        // — producers still see the row so the same day's quieter steps (or the same silence
        // episode) cannot page the family again 15 minutes later.
        await _access.RequireManageAccessAsync(requestingUserId, alert.CardiMemberId, ct);

        alert.IsActive = false;
        _unitOfWork.Alerts.Update(alert);
        await _unitOfWork.SaveChangesAsync();
    }

    /// <summary>
    /// The date filters as PostgreSQL will accept them. <c>TriggeredDate</c> is a
    /// <c>timestamp with time zone</c> and the host disables
    /// <c>Npgsql.EnableLegacyTimestampBehavior</c>, so Npgsql throws on any <see cref="DateTime"/>
    /// whose <see cref="DateTime.Kind"/> is not UTC — and the mobile "Today"/"This Week" chips
    /// send local midnight. An unspecified kind is read as UTC, the usual reading of a bare
    /// timestamp on the wire.
    /// </summary>
    private static DateTime ToUtc(DateTime instant) => instant.Kind switch
    {
        DateTimeKind.Utc => instant,
        DateTimeKind.Local => instant.ToUniversalTime(),
        _ => DateTime.SpecifyKind(instant, DateTimeKind.Utc),
    };

    private static DateTime? ToUtc(DateTime? value) => value is { } instant ? ToUtc(instant) : null;

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

    /// <summary>
    /// One signed URL per distinct member with a photo, resolved before the per-alert mapping so
    /// a page of alerts about the same member asks the storage adapter once, not once per row.
    /// The adapter caches per object name on top of that, so list refreshes don't re-sign either.
    /// </summary>
    private async Task<Dictionary<Guid, string?>> LoadPhotoUrlsAsync(
        Dictionary<Guid, CardiMember> members, CancellationToken ct)
    {
        var urls = new Dictionary<Guid, string?>();
        foreach (var (id, member) in members)
        {
            if (member.PhotoObjectName is { } photoObjectName)
                urls[id] = await _photoStorage.GetReadUrlAsync(photoObjectName, ct);
        }

        return urls;
    }

    private static AlertSummaryResponse ToSummary(Alert alert, CardiMember? member, string? photoUrl)
    {
        var rule = AlertDetailComposer.ReadRule(alert.MetricValues);
        var firedOn = DateOnly.FromDateTime(ToUtc(alert.TriggeredDate));

        return new()
        {
            AlertId = alert.Id,
            CardiMemberId = alert.CardiMemberId,
            CardiMemberName = member?.Name ?? string.Empty,
            CardiMemberPhotoUrl = photoUrl,
            EmergencyContactPhone = member?.EmergencyContactPhone,
            EmergencyContactName = member?.EmergencyContactName,
            CardiMemberPhone = member?.Phone,
            Type = alert.AlertType.GetDisplayName(),
            Severity = SeverityLabel(alert.Severity),
            Status = StatusLabel(alert),
            Title = alert.Title,
            Message = alert.Message,
            TriggeredAt = alert.TriggeredDate,
            AboutDate = AlertDetailComposer.AboutDate(rule, alert.MetricValues, firedOn),
            AcknowledgedAt = alert.AcknowledgedDate,
            AcknowledgedByUserId = alert.AcknowledgedByUserId,
        };
    }

    private static string SeverityLabel(AlertSeverity severity) =>
        severity.ToString().ToLowerInvariant();

    /// <summary>The lifecycle rule now lives in <see cref="AlertLifecycle"/>, which the
    /// dashboard's own alert strip reads too — see <see cref="AlertStatus"/>.</summary>
    private static string StatusLabel(Alert alert) => AlertLifecycle.StatusLabel(alert);
}
