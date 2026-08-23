using System.Diagnostics;
using CardiTrack.Application.Interfaces.Clients;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Services.Notifications;

public interface IDispatchService
{
    /// <summary>
    /// Writes the outbox row, then attempts delivery in-process straight away — a critical alert
    /// must not wait for the next 30-second dispatch tick. The row is written first so a crash
    /// mid-send loses nothing; <c>NotificationDispatchWorker</c> is the backstop for whatever this
    /// call doesn't finish, not the only path (§2).
    /// </summary>
    /// <remarks>
    /// One recipient per call. Fan-out across a CardiMember's caregivers (§10.3 — every caregiver
    /// with <c>ReceiveAlerts</c> gets a red alert) is the caller's job: loop and call this once per
    /// recipient user id.
    /// </remarks>
    Task<NotificationDelivery> EnqueueAsync(EnqueueRequest request, CancellationToken ct = default);

    /// <summary>
    /// Resolves an <see cref="Domain.Entities.Alert"/> to its recipients and enqueues one delivery
    /// per caregiver with <c>ReceiveAlerts</c> — the "every caregiver with ReceiveAlerts gets a red
    /// alert" fan-out from §10.3. The single entry point both the internal enqueue endpoint (AI
    /// pipeline, R2) and a same-process Worker-triggered dispatch call, so recipient resolution
    /// and delivery planning happen in exactly one place regardless of which producer created the
    /// alert — the endpoint pins the caller's identity (§7.2 C4), not which users get notified;
    /// that is always resolved server-side from <c>UserCardiMember</c>.
    /// </summary>
    Task<IReadOnlyList<NotificationDelivery>> EnqueueForAlertAsync(Guid alertId, CancellationToken ct = default);

    /// <summary>
    /// Resolves a <see cref="Domain.Entities.MemberQuestionnaire"/> to its recipients and enqueues
    /// one delivery per caregiver with <c>ReceiveAlerts</c> — the same fan-out
    /// <see cref="EnqueueForAlertAsync"/> does for an alert. Called by
    /// <c>QuestionnaireAlertWorker</c>'s sweep, both for a question's first push and for every
    /// reminder after it.
    /// </summary>
    /// <param name="occurrence">
    /// Which push this is for this question — 0 for the original ask, 1 for the first reminder, and
    /// so on. Namespaces <see cref="EnqueueRequest.DedupKey"/> per recipient per push, so a claimed
    /// row that fails after the claim still doesn't re-deliver a push already sent, while a genuine
    /// next reminder isn't deduped against the one before it.
    /// </param>
    Task<IReadOnlyList<NotificationDelivery>> EnqueueForQuestionnaireAsync(
        Guid questionnaireId, int occurrence, CancellationToken ct = default);

    /// <summary>
    /// Enqueues one all-clear per caregiver with <c>ReceiveAlerts</c> — the same fan-out
    /// <see cref="EnqueueForAlertAsync"/> does, for the opposite news. Called by
    /// <c>QuietReassuranceWorker</c>'s daily sweep once a member's silence has lasted long enough
    /// to be worth reporting.
    /// </summary>
    /// <remarks>
    /// Unlike the two above there is no row to resolve: nothing wrote down that nothing happened,
    /// so <see cref="EnqueueRequest.SourceId"/> carries the CardiMember's id and this method takes
    /// the caller's word that the stretch is real. That judgement is <c>QuietStretch</c>'s and the
    /// sweep's — re-deriving it here would put a second opinion about whether a family may be told
    /// they are fine in a type whose job is delivery.
    /// </remarks>
    /// <param name="weeklyOccurrence">
    /// Which weekly all-clear this is — 1 at seven quiet days, 2 at fourteen. Namespaces the
    /// <see cref="EnqueueRequest.DedupKey"/>, which is the whole of what paces this to one push a
    /// week per caregiver: a sweep that runs again the same day recomputes the same key and is
    /// deduped, and next week's cannot collide with it.
    /// </param>
    Task<IReadOnlyList<NotificationDelivery>> EnqueueForReassuranceAsync(
        Guid cardiMemberId, int weeklyOccurrence, CancellationToken ct = default);

    /// <summary>
    /// Re-attempts a row <c>NotificationDispatchWorker</c> claimed off the outbox. If the row
    /// already targets a specific device (a prior attempt fanned out to it), only that device is
    /// retried. If it never got that far — the original enqueue found zero live tokens — this
    /// re-resolves the user's current tokens, so a caregiver who registers a device after a red
    /// alert already fired still gets it once they do.
    /// </summary>
    Task RetryClaimedAsync(NotificationDelivery delivery, CancellationToken ct = default);
}

public sealed record EnqueueRequest(
    DeliverySourceType SourceType,
    Guid SourceId,
    Guid UserId,
    Guid? CardiMemberId,
    DeliveryCategory Category,
    AlertSeverity? Severity,
    string DedupKey,
    string? CollapseKey,
    AlertType? AlertType = null);

/// <summary>
/// The "immediate send, durable retry" orchestration from §2. Awaited within the caller's request
/// or job scope — <b>never <c>Task.Run</c> fire-and-forget</b>: detached background work in the
/// API would be a scheduled-job-outside-the-Worker violation of the binding CLAUDE.md rule, and
/// would be lost on instance shutdown besides. If awaiting ever costs too much request latency,
/// the fix is to let the dispatch loop take it, not to detach it.
/// </summary>
public class DispatchService : IDispatchService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly INotificationChannel _channel;
    private readonly INotificationPreferenceService _preferences;
    private readonly INotificationGapResolver _gapResolver;
    private readonly TimeProvider _timeProvider;

    public DispatchService(
        IUnitOfWork unitOfWork,
        INotificationChannel channel,
        INotificationPreferenceService preferences,
        INotificationGapResolver gapResolver,
        TimeProvider? timeProvider = null)
    {
        _unitOfWork = unitOfWork;
        _channel = channel;
        _preferences = preferences;
        _gapResolver = gapResolver;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<NotificationDelivery> EnqueueAsync(EnqueueRequest request, CancellationToken ct = default)
    {
        using var activity = PushDispatchTelemetry.Source.StartActivity("notification.enqueue", ActivityKind.Internal);
        activity?.SetTag(PushDispatchTelemetry.CategoryTag, request.Category.ToString());

        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;

        // Dedup — namespaced per producer (§6.2). An existing live row for the same key means this
        // is a re-run of detection, not a new event; return it unchanged rather than double-send.
        var existing = await _unitOfWork.NotificationDeliveries.GetByDedupKeyAsync(request.DedupKey, ct);
        if (existing is not null)
        {
            activity?.SetTag(PushDispatchTelemetry.DeliveryIdTag, existing.Id.ToString());
            activity?.SetTag(PushDispatchTelemetry.DedupHitTag, true);
            return existing;
        }
        activity?.SetTag(PushDispatchTelemetry.DedupHitTag, false);

        var user = await _unitOfWork.Users.GetByIdAsync(request.UserId);
        var timeZoneId = user?.TimeZoneId ?? "UTC";
        var (isWithinQuietHours, quietHoursEndUtc) =
            await _preferences.EvaluateQuietHoursAsync(request.UserId, timeZoneId, utcNow, ct);

        var plan = DeliveryPlanner.Plan(new DeliveryPlanningContext
        {
            UtcNow = utcNow,
            Category = request.Category,
            Severity = request.Severity,
            DedupKey = request.DedupKey,
            CollapseKey = request.CollapseKey,
            IsWithinQuietHours = isWithinQuietHours,
            QuietHoursEndUtc = quietHoursEndUtc
        });

        var delivery = new NotificationDelivery
        {
            SourceType = request.SourceType,
            SourceId = request.SourceId,
            UserId = request.UserId,
            CardiMemberId = request.CardiMemberId,
            Category = request.Category,
            Severity = request.Severity,
            AlertType = request.AlertType,
            Channel = plan.Channel,
            State = DeliveryState.Pending,
            DedupKey = plan.DedupKey,
            CollapseKey = plan.CollapseKey,
            ExpiresAt = plan.ExpiresAt,
            ScheduledFor = plan.ScheduledFor
        };

        await _unitOfWork.NotificationDeliveries.AddAsync(delivery);
        await _unitOfWork.SaveChangesAsync();

        activity?.SetTag(PushDispatchTelemetry.DeliveryIdTag, delivery.Id.ToString());
        activity?.SetTag(PushDispatchTelemetry.ChannelTag, plan.Channel.ToString());

        // In-app only, or deferred by quiet hours — the dispatch worker's 30-second loop picks it
        // up when it's due. Nothing to attempt right now.
        if (plan.Channel != DeliveryChannel.Push || plan.ScheduledFor is not null)
            return delivery;

        await AttemptSendToAllDevicesAsync(delivery, request.UserId, ct);
        return delivery;
    }

    public async Task<IReadOnlyList<NotificationDelivery>> EnqueueForAlertAsync(Guid alertId, CancellationToken ct = default)
    {
        // A missing alert is the caller's concern to log — Application carries no logging
        // package (the zero-package-core invariant), so this stays a silent empty result and the
        // API/Worker caller, which does have a logger, is where this would be surfaced.
        var alert = await _unitOfWork.Alerts.GetByIdAsync(alertId);
        if (alert is null)
            return [];

        var links = await _unitOfWork.UserCardiMembers.GetByCardiMemberIdAsync(alert.CardiMemberId);
        var recipients = links.Where(l => l.IsActive && l.ReceiveAlerts).Select(l => l.UserId).Distinct().ToList();

        var results = new List<NotificationDelivery>(recipients.Count);
        foreach (var userId in recipients)
        {
            var request = new EnqueueRequest(
                SourceType: DeliverySourceType.Alert,
                SourceId: alert.Id,
                UserId: userId,
                CardiMemberId: alert.CardiMemberId,
                Category: DeliveryCategory.Health,
                Severity: alert.Severity,
                // Alert rows are already the dedup boundary — one Alert produces one delivery per
                // recipient regardless of which producer (Worker in R1, AI pipeline in R2) raised
                // it, unlike the device-silence case in §6.2 where two producers can race.
                DedupKey: $"alert:{alert.Id}:{userId}",
                CollapseKey: $"alert-{alert.Id}",
                AlertType: alert.AlertType);

            results.Add(await EnqueueAsync(request, ct));
        }

        return results;
    }

    public async Task<IReadOnlyList<NotificationDelivery>> EnqueueForQuestionnaireAsync(
        Guid questionnaireId, int occurrence, CancellationToken ct = default)
    {
        // A missing questionnaire is the caller's concern to log — same stance EnqueueForAlertAsync
        // takes, for the same zero-package-core reason.
        var questionnaire = await _unitOfWork.MemberQuestionnaires.GetByIdAsync(questionnaireId);
        if (questionnaire is null)
            return [];

        // Re-checked here rather than trusted from the caller's claim: this is a shared entry
        // point, not one only QuestionnaireAlertWorker's guarded claim can reach, and even from
        // that caller there is a — small — window after the claim where the family could have
        // answered or dismissed the question before this runs. A push for a question that is no
        // longer live would reach a caregiver about something already settled.
        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        if (questionnaire.Status != QuestionnaireStatus.Pending || questionnaire.HasLapsed(utcNow))
            return [];

        var links = await _unitOfWork.UserCardiMembers.GetByCardiMemberIdAsync(questionnaire.CardiMemberId);
        var recipients = links.Where(l => l.IsActive && l.ReceiveAlerts).Select(l => l.UserId).Distinct().ToList();

        var results = new List<NotificationDelivery>(recipients.Count);
        foreach (var userId in recipients)
        {
            var request = new EnqueueRequest(
                SourceType: DeliverySourceType.Questionnaire,
                SourceId: questionnaire.Id,
                UserId: userId,
                CardiMemberId: questionnaire.CardiMemberId,
                Category: DeliveryCategory.Questionnaire,
                Severity: null,
                DedupKey: $"questionnaire:{questionnaire.Id}:{userId}:{occurrence}",
                // Constant across occurrences, unlike the dedup key: a reminder should replace the
                // original push on the device rather than stack beneath it.
                CollapseKey: $"questionnaire-{questionnaire.Id}");

            results.Add(await EnqueueAsync(request, ct));
        }

        return results;
    }

    public async Task<IReadOnlyList<NotificationDelivery>> EnqueueForReassuranceAsync(
        Guid cardiMemberId, int weeklyOccurrence, CancellationToken ct = default)
    {
        // The only re-check worth making here, and the same silent-empty stance the two methods
        // above take on a missing row: a member removed between the sweep reading them and this
        // call must not have their family told they are fine. Everything else about the verdict
        // is the caller's — see the interface.
        var member = await _unitOfWork.CardiMembers.GetByIdAsync(cardiMemberId);
        if (member is null || !member.IsActive)
            return [];

        var links = await _unitOfWork.UserCardiMembers.GetByCardiMemberIdAsync(cardiMemberId);
        // ReceiveAlerts, the same flag the bad news honours: a caregiver who asked not to hear
        // about this member did not ask to hear about them weekly instead.
        var recipients = links.Where(l => l.IsActive && l.ReceiveAlerts).Select(l => l.UserId).Distinct().ToList();

        var results = new List<NotificationDelivery>(recipients.Count);
        foreach (var userId in recipients)
        {
            var request = new EnqueueRequest(
                SourceType: DeliverySourceType.Reassurance,
                // No row exists, so the member is the subject — which is also what the FCM deep
                // link needs to open, so nothing has to be looked up at send time.
                SourceId: cardiMemberId,
                UserId: userId,
                CardiMemberId: cardiMemberId,
                Category: DeliveryCategory.Reassurance,
                Severity: null,
                DedupKey: $"reassurance:{cardiMemberId}:{userId}:{weeklyOccurrence}",
                // Constant across weeks, unlike the dedup key: this week's all-clear should
                // replace last week's on the device rather than stack a pile of identical good
                // news in the shade.
                CollapseKey: $"reassurance-{cardiMemberId}");

            results.Add(await EnqueueAsync(request, ct));
        }

        return results;
    }

    public async Task RetryClaimedAsync(NotificationDelivery delivery, CancellationToken ct = default)
    {
        // A row that reached a terminal state between being claimed and being retried has
        // nothing left for this method to do — the claim lease (see
        // NotificationDeliveryRepository.ClaimDueAsync) just expires unused. Sent is
        // deliberately NOT terminal here: the escalation sweep calls this method on a Sent row
        // to perform the 120s repush (EscalationAction.Repush in NotificationDispatchWorker), so
        // returning early on Sent would silently turn every repush into a no-op.
        if (delivery.State is DeliveryState.Delivered or DeliveryState.DeadLettered
            or DeliveryState.Undelivered or DeliveryState.Suppressed)
            return;

        if (RetryBackoffPolicy.IsExhausted(delivery.Attempts))
        {
            delivery.State = DeliveryState.DeadLettered;
            delivery.LastError = "Retry attempts exhausted.";
            _unitOfWork.NotificationDeliveries.Update(delivery);
            await _unitOfWork.SaveChangesAsync();
            return;
        }

        if (delivery.PushDeviceTokenId is { } tokenId)
        {
            var token = await _unitOfWork.PushDeviceTokens.GetByIdAsync(tokenId);
            if (token is null || token.DisabledDate is not null)
            {
                // The device this row was aimed at is gone — nothing left to retry it against.
                delivery.State = DeliveryState.DeadLettered;
                delivery.LastError = "Target device token disabled.";
                _unitOfWork.NotificationDeliveries.Update(delivery);
                await _unitOfWork.SaveChangesAsync();
                return;
            }

            await SendOneAttemptAsync(delivery, token, ct);
            _unitOfWork.NotificationDeliveries.Update(delivery);
            await _unitOfWork.SaveChangesAsync();
            return;
        }

        // Never got a device the first time — re-resolve now, in case one registered since.
        await AttemptSendToAllDevicesAsync(delivery, delivery.UserId, ct);
    }

    /// <summary>
    /// One <see cref="NotificationDelivery"/> row targets one <see cref="PushDeviceToken"/>, but a
    /// user can have several devices — this fans the single planned row out to a send attempt per
    /// live token, cloning the row for every device after the first so each carries its own
    /// per-device ack token later.
    /// </summary>
    private async Task AttemptSendToAllDevicesAsync(NotificationDelivery delivery, Guid userId, CancellationToken ct)
    {
        var tokens = await _unitOfWork.PushDeviceTokens.GetLiveForUserAsync(userId, delivery.Category, ct);
        if (tokens.Count == 0)
        {
            // Nothing to send to right now — leave it Pending for the dispatch worker's retry
            // loop, which also arms PUSH_UNREACHABLE via DeviceTokenService's reconciliation path
            // once a registration or unregistration changes the picture.
            return;
        }

        var first = true;
        foreach (var token in tokens)
        {
            var target = first ? delivery : Clone(delivery);
            first = false;

            target.PushDeviceTokenId = token.Id;

            await SendOneAttemptAsync(target, token, ct);

            if (target != delivery)
                await _unitOfWork.NotificationDeliveries.AddAsync(target);
            else
                _unitOfWork.NotificationDeliveries.Update(target);
        }

        await _unitOfWork.SaveChangesAsync();
    }

    /// <summary>
    /// Wraps one FCM send + its outcome in a <c>notification.attempt</c> span (parent of
    /// <c>FcmNotificationChannel</c>'s own <c>fcm.send</c> span), and stamps the delivery with this
    /// span's id — a W3C traceparent — so the eventual client ack, which can arrive independently
    /// up to the escalation ladder's 900s ceiling later, can link back to the trace that sent it.
    /// </summary>
    private async Task SendOneAttemptAsync(NotificationDelivery target, PushDeviceToken token, CancellationToken ct)
    {
        using var activity = PushDispatchTelemetry.Source.StartActivity("notification.attempt", ActivityKind.Internal);
        activity?.SetTag(PushDispatchTelemetry.DeliveryIdTag, target.Id.ToString());
        // 1-based: target.Attempts counts prior failures (starts at 0), but the tag is read as a
        // human-facing ordinal in trace UIs/dashboards — "attempt 1" for the first try, not "0".
        activity?.SetTag(PushDispatchTelemetry.AttemptNumberTag, target.Attempts + 1);

        target.SendTraceParent = activity?.Id;

        var result = await _channel.SendAsync(target, token, ct);
        await ApplyAsync(target, token, result, ct);

        activity?.SetTag(PushDispatchTelemetry.StateTag, target.State.ToString());
    }

    private async Task ApplyAsync(
        NotificationDelivery delivery, PushDeviceToken token, SendResult result, CancellationToken ct)
    {
        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;

        switch (result)
        {
            case SendResult.Sent sent:
                delivery.State = DeliveryState.Sent;
                delivery.ProviderMessageId = sent.ProviderMessageId;
                // Only the first successful send stamps SentDate — this method also runs the
                // escalation ladder's 120s repush (RetryClaimedAsync on an already-Sent row), and
                // the whole ladder's boundary math (EscalationPolicy) is elapsed time *since the
                // original send*. Overwriting it on every repush would reset that clock and the
                // 300s/900s stages would never fire.
                delivery.SentDate ??= utcNow;
                break;

            case SendResult.Retryable retryable:
                delivery.State = DeliveryState.Failed;
                delivery.LastError = retryable.Reason;
                // Compute the delay from the attempt count BEFORE incrementing — Attempts starts
                // at 0, and RetryBackoffPolicy.NextDelay(0) is the schedule's first (15s) entry.
                // Incrementing first would skip straight to the second (60s) entry on the very
                // first failure.
                delivery.NextAttemptAt = utcNow + (RetryBackoffPolicy.NextDelay(delivery.Attempts) ?? TimeSpan.Zero);
                delivery.Attempts++;
                break;

            case SendResult.Permanent permanent:
                // FCM UNREGISTERED / APNs 410 — the token is dead. Disabling it immediately arms
                // PUSH_UNREACHABLE if it was this user's last live token: the failure feeds the
                // engine back (§6.2).
                delivery.State = DeliveryState.DeadLettered;
                delivery.LastError = permanent.Reason;
                DeviceTokenService.Disable(token, $"FCM permanent failure: {permanent.Reason}");
                _unitOfWork.PushDeviceTokens.Update(token);
                await _gapResolver.ResolveForUserAsync(delivery.UserId, ct);
                break;
        }
    }

    private static NotificationDelivery Clone(NotificationDelivery source) => new()
    {
        SourceType = source.SourceType,
        SourceId = source.SourceId,
        UserId = source.UserId,
        CardiMemberId = source.CardiMemberId,
        Category = source.Category,
        Severity = source.Severity,
        AlertType = source.AlertType,
        Channel = source.Channel,
        State = source.State,
        // A cloned per-device row shares the semantic dedup key but not the DB-unique one — the
        // suffix keeps the unique index happy without changing what "the same alert" means.
        DedupKey = $"{source.DedupKey}:{Guid.NewGuid():N}",
        CollapseKey = source.CollapseKey,
        ExpiresAt = source.ExpiresAt,
        ScheduledFor = source.ScheduledFor
    };
}
