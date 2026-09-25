using CardiTrack.Application.DTOs.Common;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Exceptions;
using CardiTrack.Application.Interfaces.Clients;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Security;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Services.Notifications;
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
    private readonly IEncryptionService _encryption;
    private readonly IAckDeliveryService _ackDelivery;
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
        IEncryptionService encryption,
        IAckDeliveryService ackDelivery,
        TimeProvider? timeProvider = null)
    {
        _unitOfWork = unitOfWork;
        _access = access;
        _photoStorage = photoStorage;
        _encryption = encryption;
        _ackDelivery = ackDelivery;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<AlertListResponse> GetAlertsAsync(
        Guid requestingUserId,
        Guid? cardiMemberId = null,
        AlertSeverity? severity = null,
        AlertStatusFilter? status = null,
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
        TimeZoneInfo? zone = null;

        if (days > 0)
        {
            // The member's anchor clock, not UTC — the same one StatisticalAlertService evaluated
            // the rule on. Reading an alert back on a different calendar than the one that raised
            // it is how a caregiver ends up looking at a window whose "yesterday" is not the
            // yesterday the alert is about. Resolved only when a daily window is actually being
            // plotted: it costs a links query plus a user read, and device-silence and the
            // realtime-HR rule never look at a calendar day.
            zone = await MemberAnchorTimeZone.ResolveAsync(_unitOfWork, alert.CardiMemberId);
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

        // The stored explanation, if the pass that raised this alert got to it. One indexed
        // lookup — never a model call on the request path, which is the whole reason it is stored.
        var narrative = await _unitOfWork.MemberInsights.GetForAlertAsync(alertId);

        var detail = AlertDetailComposer.Compose(
            alert, member, acknowledger, logs, today, granular, baseline, elapsedSteps, firedOn,
            photoUrl, zone);

        if (InsightServability.IsServable(narrative, utcNow))
        {
            detail.Narrative = new AlertNarrativeResponse
            {
                Explanation = narrative.Summary,
                RecommendedAction = narrative.RecommendedAction,
                GeneratedAt = narrative.GeneratedAtUtc,
            };
        }

        await AttachAnswersAsync(detail, alert, rule, ct);

        return detail;
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
        Guid requestingUserId, Guid alertId, CancellationToken ct = default) =>
        await AcknowledgeAsync(requestingUserId, alertId, responseCode: null, note: null, ct);

    public async Task<AlertAcknowledgementResponse> AcknowledgeAsync(
        Guid requestingUserId, Guid alertId, string? responseCode, string? note,
        CancellationToken ct = default)
    {
        var alert = await _unitOfWork.Alerts.GetByIdWithCardiMemberAsync(alertId);
        if (alert is null || !alert.IsActive)
            throw new KeyNotFoundException("Alert not found");

        await _access.RequireViewAccessAsync(requestingUserId, alert.CardiMemberId, ct);

        var response = BuildResponse(
            alert, requestingUserId, AlertResponseKind.Acknowledge, responseCode, note);

        // Idempotent on purpose: two family members tapping "handled" seconds apart is the
        // expected case, and the second tap must not overwrite who actually dealt with it. The
        // response row is appended either way — the second caregiver still said something, and
        // losing it is exactly the coordination the whole table exists to keep.
        var alertChanged = alert.AcknowledgedDate is null;
        if (alertChanged)
        {
            alert.AcknowledgedDate = _timeProvider.GetUtcNow().UtcDateTime;
            alert.AcknowledgedByUserId = requestingUserId;
            _unitOfWork.Alerts.Update(alert);
        }

        await SaveAnswerAsync(response, alertChanged, ct);

        // After the save, so a crash between the two leaves the family still being chased about
        // an alert rather than an unrecorded answer to one nobody is chasing.
        await _ackDelivery.HaltEscalationForAlertAsync(alert.Id, ct);

        return await AnswerResultAsync(alert, requestingUserId, response, ct);
    }

    public async Task<AlertAcknowledgementResponse> CloseAsync(
        Guid requestingUserId, Guid alertId, string? responseCode, string? note,
        CancellationToken ct = default)
    {
        var alert = await _unitOfWork.Alerts.GetByIdWithCardiMemberAsync(alertId);
        if (alert is null || !alert.IsActive)
            throw new KeyNotFoundException("Alert not found");

        // View access, the same bar acknowledging asks. Closing is a bigger claim, but it is a
        // claim about what the caregiver did, and the person who checked on the member is often
        // not the one who can edit their record.
        await _access.RequireViewAccessAsync(requestingUserId, alert.CardiMemberId, ct);

        var response = BuildResponse(
            alert, requestingUserId, AlertResponseKind.Close, responseCode, note);

        // First close wins the attribution, like acknowledgement — but unlike acknowledgement
        // there is no undo, so two caregivers closing within seconds must not be an error for
        // the second. Their response is kept and the screen can say who got there first.
        //
        // An alert CardiTrack already resolved because the condition passed keeps its null
        // ResolvedByUserId: they may still want to say what happened, and crediting them with a
        // resolution the product made would be the wrong record.
        var alertChanged = !alert.IsResolved;
        if (alertChanged)
        {
            alert.IsResolved = true;
            alert.ResolvedByUserId = requestingUserId;
            alert.AcknowledgedDate ??= _timeProvider.GetUtcNow().UtcDateTime;
            alert.AcknowledgedByUserId ??= requestingUserId;
            _unitOfWork.Alerts.Update(alert);
        }

        await SaveAnswerAsync(response, alertChanged, ct);
        await _ackDelivery.HaltEscalationForAlertAsync(alert.Id, ct);

        return await AnswerResultAsync(alert, requestingUserId, response, ct);
    }

    /// <summary>
    /// Validates the canned code against this alert's rule and encrypts the note — everything a
    /// response row needs before anything is written.
    /// </summary>
    /// <remarks>
    /// Built before the alert is touched so an invalid code fails the whole request rather than
    /// leaving an alert marked handled with no record of who handled it.
    /// </remarks>
    private AlertResponse BuildResponse(
        Alert alert, Guid userId, AlertResponseKind kind, string? responseCode, string? note)
    {
        var rule = AlertDetailComposer.ReadRule(alert.MetricValues);
        var catalogKind = kind == AlertResponseKind.Close
            ? AlertResponseCatalog.ResponseKind.Close
            : AlertResponseCatalog.ResponseKind.Acknowledge;

        if (!AlertResponseCatalog.IsValid(rule, catalogKind, responseCode))
        {
            var valid = AlertResponseCatalog.CodesFor(rule, catalogKind);
            throw new AlertResponseCodeException(
                $"\"{responseCode}\" isn't one of the answers for this alert. Try one of: {string.Join(", ", valid)}.",
                valid);
        }

        return new AlertResponse
        {
            AlertId = alert.Id,
            UserId = userId,
            Kind = kind,
            ResponseCode = string.IsNullOrWhiteSpace(responseCode) ? null : responseCode,
            // Health information about a named person, so encrypted at rest exactly as
            // CardiMember.MedicalNotes is. No legacy-plaintext fallback on the way back out: this
            // column is new, so every value in it was written by this code path.
            Note = string.IsNullOrWhiteSpace(note) ? null : _encryption.Encrypt(note.Trim()),
            // The injected clock, not BaseEntity's DateTime.UtcNow, so "Tom closed this 10 s ago"
            // is measured against the same clock that stamped the alert's own acknowledgement.
            CreatedDate = _timeProvider.GetUtcNow().UtcDateTime,
        };
    }

    /// <summary>
    /// Commits the alert's own change and the response row together — one transaction, because an
    /// alert marked handled with no record of who handled it is worse than neither.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A response carrying neither a code nor a note is not written at all. The bodiless
    /// acknowledge that already shipped is exactly that case, and a row saying only "somebody
    /// tapped something" would pad every alert's history with entries that answer nothing — the
    /// alert's own <c>AcknowledgedByUserId</c> already records that tap.
    /// </para>
    /// <para>
    /// Which leaves the case where there is nothing at all to write: a second caregiver tapping
    /// "handled" with no body on an alert the first already handled. That saves nothing, the same
    /// way it always did.
    /// </para>
    /// </remarks>
    private async Task SaveAnswerAsync(AlertResponse response, bool alertChanged, CancellationToken ct)
    {
        var appending = response.ResponseCode is not null || response.Note is not null;
        if (appending)
            await _unitOfWork.AlertResponses.AddAsync(response);

        if (appending || alertChanged)
            await _unitOfWork.SaveChangesAsync();
    }

    private async Task<AlertAcknowledgementResponse> AnswerResultAsync(
        Alert alert, Guid requestingUserId, AlertResponse response, CancellationToken ct)
    {
        var unread = await _unitOfWork.Alerts.CountUnreadAsync(
            await _access.GetViewableMemberIdsAsync(requestingUserId, ct), ct);

        var recorded = response.ResponseCode is not null || response.Note is not null;

        return new AlertAcknowledgementResponse
        {
            AlertId = alert.Id,
            Status = StatusLabel(alert),
            AcknowledgedAt = alert.AcknowledgedDate,
            AcknowledgedByUserId = alert.AcknowledgedByUserId,
            ResolvedByUserId = alert.ResolvedByUserId,
            UnreadCount = unread,
            Response = recorded
                ? await ProjectResponseAsync(alert, response, ct)
                : null,
            FamilyNotified = recorded
                ? await OtherCaregiverCountAsync(alert.CardiMemberId, requestingUserId, ct)
                : 0,
        };
    }

    /// <summary>
    /// How many other caregivers on this member will see the answer. Read after the write so a
    /// caregiver removed mid-request is not counted.
    /// </summary>
    private async Task<int> OtherCaregiverCountAsync(
        Guid cardiMemberId, Guid responderUserId, CancellationToken ct)
    {
        var links = await _unitOfWork.UserCardiMembers.GetByCardiMemberIdAsync(cardiMemberId);
        return links.Count(l => l.IsActive && l.UserId != responderUserId);
    }

    private async Task<AlertResponseEntry> ProjectResponseAsync(
        Alert alert, AlertResponse response, CancellationToken ct)
    {
        var responder = response.UserId is { } userId
            ? await _unitOfWork.Users.GetByIdAsync(userId)
            : null;
        return ProjectResponse(
            response, AlertDetailComposer.ReadRule(alert.MetricValues), responder?.Name);
    }

    /// <summary>
    /// One stored response as the wire carries it: the note decrypted, the code's current label
    /// resolved, and the responder named.
    /// </summary>
    private AlertResponseEntry ProjectResponse(
        AlertResponse response, string? rule, string? responderName)
    {
        var catalogKind = response.Kind == AlertResponseKind.Close
            ? AlertResponseCatalog.ResponseKind.Close
            : AlertResponseCatalog.ResponseKind.Acknowledge;

        return new AlertResponseEntry
        {
            Id = response.Id,
            Kind = response.Kind.ToString().ToLowerInvariant(),
            UserId = response.UserId,
            // An erased account still gave this answer, and the line reads as a claim either way
            // — better unattributed than attributed to nobody.
            UserName = string.IsNullOrWhiteSpace(responderName) ? "Someone" : responderName,
            ResponseCode = response.ResponseCode,
            ResponseLabel = response.ResponseCode is null
                ? null
                : AlertResponseCatalog.For(rule, catalogKind)
                    .FirstOrDefault(o => o.Code == response.ResponseCode)?.Label,
            Note = Reveal(response.Note),
            CreatedAt = response.CreatedDate,
        };
    }

    /// <summary>
    /// The note as it was typed. Unlike <c>CardiMemberService.Reveal</c> there is no
    /// legacy-plaintext fallback, because this column never held any: every value in it was
    /// written encrypted by <see cref="BuildResponse"/>. A value that will not decrypt is
    /// genuinely unreadable, and reading it back as though it were plain text would print
    /// ciphertext into a caregiver's screen as if somebody had typed it.
    /// </summary>
    private string? Reveal(string? stored) =>
        string.IsNullOrEmpty(stored) ? null : _encryption.Decrypt(stored);

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

            // Acknowledging stopped the ladder, so un-acknowledging has to start it again.
            // Otherwise undo returns the alert to unhandled while every delivery about it stays
            // terminal: it reads as live on every screen and nothing at all is chasing it, which
            // is worse than leaving it acknowledged would have been.
            await _ackDelivery.ResumeEscalationForAlertAsync(alert.Id, ct);
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
            CardiMemberName = member?.FullName ?? string.Empty,
            CardiMemberFirstName = member?.FirstName ?? string.Empty,
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
    /// <summary>
    /// Hangs "what the family did" and the chips to answer with onto a composed detail payload.
    /// </summary>
    /// <remarks>
    /// One extra query for the responses and one name lookup per distinct responder — bounded by
    /// how many people watch the member, which is a handful. The options come from a static
    /// catalogue and cost nothing.
    /// </remarks>
    private async Task AttachAnswersAsync(
        AlertDetailResponse detail, Alert alert, string? rule, CancellationToken ct)
    {
        detail.ResponseOptions = new AlertResponseOptionsResponse
        {
            Acknowledge = Options(rule, AlertResponseCatalog.ResponseKind.Acknowledge),
            Close = Options(rule, AlertResponseCatalog.ResponseKind.Close),
        };

        detail.ResolvedByUserId = alert.ResolvedByUserId;
        if (alert.ResolvedByUserId is { } resolver)
            detail.ResolvedByName = (await _unitOfWork.Users.GetByIdAsync(resolver))?.Name;

        var responses = await _unitOfWork.AlertResponses.GetForAlertAsync(alert.Id, ct);
        if (responses.Count == 0)
            return;

        var names = new Dictionary<Guid, string?>();
        foreach (var userId in responses.Select(r => r.UserId).OfType<Guid>().Distinct())
            names[userId] = (await _unitOfWork.Users.GetByIdAsync(userId))?.Name;

        detail.Responses = responses
            .Select(r => ProjectResponse(
                r, rule, r.UserId is { } id ? names.GetValueOrDefault(id) : null))
            .ToList();
    }

    private static List<AlertResponseOptionResponse> Options(
        string? rule, AlertResponseCatalog.ResponseKind kind) =>
        AlertResponseCatalog.For(rule, kind)
            .Select(o => new AlertResponseOptionResponse { Code = o.Code, Label = o.Label })
            .ToList();

    private static string StatusLabel(Alert alert) => AlertLifecycle.StatusLabel(alert);
}
