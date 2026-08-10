using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Services.Notifications;

/// <inheritdoc cref="INotificationService"/>
public class NotificationService : INotificationService
{
    /// <summary>
    /// Matches <c>CardiMemberAccessService.DeniedMessage</c> in spirit: a caller asking about
    /// somebody else's notification is told the same thing as a caller asking about one that does
    /// not exist, so the response cannot be used to enumerate.
    /// </summary>
    internal const string NotFoundMessage = "Notification not found";

    /// <summary>Two slots on the dashboard. A third card is a list, and a list is not a prompt.</summary>
    public const int DashboardCardLimit = 2;

    private readonly IUnitOfWork _unitOfWork;
    private readonly INotificationGapResolver _gapResolver;
    private readonly TimeProvider _timeProvider;

    public NotificationService(
        IUnitOfWork unitOfWork,
        INotificationGapResolver gapResolver,
        TimeProvider? timeProvider = null)
    {
        _unitOfWork = unitOfWork;
        _gapResolver = gapResolver;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

    public async Task<NotificationListResponse> GetInboxAsync(
        Guid requestingUserId,
        NotificationState? state,
        NotificationCategory? category,
        Guid? cardiMemberId,
        bool? owned,
        int limit,
        int offset,
        CancellationToken ct = default)
    {
        var rows = await _unitOfWork.Notifications.QueryAsync(
            requestingUserId, state, category, cardiMemberId, owned, limit, offset, ct);
        var total = await _unitOfWork.Notifications.CountAsync(
            requestingUserId, state, category, cardiMemberId, owned, ct);

        return new NotificationListResponse
        {
            Items = await ProjectAsync(rows, ct),
            TotalCount = total,
            Limit = limit,
            Offset = offset
        };
    }

    public async Task<NotificationSummaryResponse> GetSummaryAsync(
        Guid requestingUserId, CancellationToken ct = default)
    {
        var visible = await _unitOfWork.Notifications.GetTopForDashboardAsync(
            requestingUserId, limit: 20, UtcNow, ct);

        var projected = await ProjectAsync(visible, ct);

        return new NotificationSummaryResponse
        {
            UnseenCount = await _unitOfWork.Notifications.CountUnseenAsync(requestingUserId, ct),
            OpenCount = projected.Count,
            SafetyBanners = [.. projected.Where(n => n.Category == NotificationCategory.Safety)],
            DashboardCards =
            [
                .. projected
                    .Where(n => n.Category != NotificationCategory.Safety)
                    .Take(DashboardCardLimit)
            ]
        };
    }

    public async Task MarkSeenAsync(Guid requestingUserId, Guid notificationId, CancellationToken ct = default)
    {
        var notification = await RequireOwnAsync(requestingUserId, notificationId);

        // First sighting only — re-opening the inbox must not reset the funnel's clock.
        if (notification.FirstSeenDate is not null)
            return;

        notification.FirstSeenDate = UtcNow;
        _unitOfWork.Notifications.Update(notification);
        await _unitOfWork.SaveChangesAsync();
    }

    public async Task<NotificationResponse> SnoozeAsync(
        Guid requestingUserId, Guid notificationId, TimeSpan? duration, CancellationToken ct = default)
    {
        var notification = await RequireOwnAsync(requestingUserId, notificationId);
        var rule = NudgeRuleCatalogue.Find(notification.RuleCode);

        var requested = duration ?? rule?.Spec.DefaultSnooze ?? TimeSpan.FromDays(7);
        var max = rule?.Spec.MaxSnooze ?? TimeSpan.FromDays(30);

        // Clamped rather than rejected: a client asking for a month on a Safety rule gets 72 hours
        // and a successful response, not an error it has to interpret.
        var granted = requested > max ? max : requested;

        notification.State = NotificationState.Snoozed;
        notification.SnoozedUntil = UtcNow + granted;
        _unitOfWork.Notifications.Update(notification);
        await _unitOfWork.SaveChangesAsync();

        return (await ProjectAsync([notification], ct))[0];
    }

    public async Task DismissAsync(
        Guid requestingUserId, Guid notificationId, bool acknowledgedConsequence, CancellationToken ct = default)
    {
        var notification = await RequireOwnAsync(requestingUserId, notificationId);
        var rule = NudgeRuleCatalogue.Find(notification.RuleCode);
        var canMute = rule?.Spec.CanMute ?? true;

        if (!canMute && !acknowledgedConsequence)
        {
            throw new InvalidOperationException(
                "This alert can't be turned off without confirming what it means.");
        }

        var existing = await _unitOfWork.NotificationMutes.FindForScopeAsync(
            requestingUserId, notification.RuleCode, null, notification.CardiMemberId, ct);

        if (existing is null)
        {
            await _unitOfWork.NotificationMutes.AddAsync(new NotificationMute
            {
                UserId = requestingUserId,
                RuleCode = notification.RuleCode,
                CardiMemberId = notification.CardiMemberId,
                MutedDate = UtcNow,
                AcknowledgedConsequence = !canMute && acknowledgedConsequence
            });
        }

        notification.State = NotificationState.Resolved;
        notification.ResolutionReason = NotificationResolutionReason.Dismissed;
        notification.ResolvedDate = UtcNow;
        _unitOfWork.Notifications.Update(notification);

        await _unitOfWork.SaveChangesAsync();
    }

    public async Task<IReadOnlyList<NotificationMuteResponse>> GetMutesAsync(
        Guid requestingUserId, CancellationToken ct = default)
    {
        var mutes = await _unitOfWork.NotificationMutes.GetByUserAsync(requestingUserId, ct);
        var live = mutes.Where(m => m.IsInEffect(UtcNow)).ToList();
        var names = await ResolveNamesAsync(live.Select(m => m.CardiMemberId));

        return
        [
            .. live.Select(m => new NotificationMuteResponse
            {
                Id = m.Id,
                RuleCode = m.RuleCode,
                Category = m.Category,
                CardiMemberId = m.CardiMemberId,
                CardiMemberName = Lookup(names, m.CardiMemberId),
                MutedDate = m.MutedDate,
                MutedUntil = m.MutedUntil
            })
        ];
    }

    public async Task RemoveMuteAsync(Guid requestingUserId, Guid muteId, CancellationToken ct = default)
    {
        var mute = await _unitOfWork.NotificationMutes.GetByIdAsync(muteId);
        if (mute is null || mute.UserId != requestingUserId)
            throw new KeyNotFoundException(NotFoundMessage);

        _unitOfWork.NotificationMutes.Remove(mute);
        await _unitOfWork.SaveChangesAsync();

        // Un-muting should bring the gap straight back if it is still open, rather than leaving the
        // user waiting until tomorrow's run to find out whether anything was behind the mute.
        await _gapResolver.ResolveForUserAsync(requestingUserId, ct);
    }

    public async Task<int> ResetMutesAsync(Guid requestingUserId, CancellationToken ct = default)
    {
        var cleared = await _unitOfWork.NotificationMutes.ClearAllForUserAsync(requestingUserId, ct);
        await _gapResolver.ResolveForUserAsync(requestingUserId, ct);
        return cleared;
    }

    /// <summary>
    /// Loads a notification, or reports it missing when it belongs to someone else. A row carried
    /// for visibility only (<see cref="Notification.IsOwner"/> false) is refused the same way: a
    /// relative may see a family item, but snoozing and dismissing it is the owner's job, and
    /// reporting it missing rather than forbidden keeps the two cases indistinguishable to a prober.
    /// </summary>
    private async Task<Notification> RequireOwnAsync(Guid requestingUserId, Guid notificationId)
    {
        var notification = await _unitOfWork.Notifications.GetByIdAsync(notificationId);

        if (notification is null
            || notification.UserId != requestingUserId
            || !notification.IsOwner
            || !notification.IsActive)
            throw new KeyNotFoundException(NotFoundMessage);

        return notification;
    }

    /// <summary>
    /// Renders stored rows for the wire, resolving member names here rather than reading them from
    /// the row. The row holds counters only — see <see cref="Notification.TemplateData"/>.
    /// </summary>
    private async Task<List<NotificationResponse>> ProjectAsync(
        IReadOnlyList<Notification> rows, CancellationToken ct)
    {
        if (rows.Count == 0)
            return [];

        var names = await ResolveNamesAsync(rows.Select(r => r.CardiMemberId));

        return
        [
            .. rows.Select(r =>
            {
                var rule = NudgeRuleCatalogue.Find(r.RuleCode);
                return new NotificationResponse
                {
                    Id = r.Id,
                    RuleCode = r.RuleCode,
                    Category = r.Category,
                    Priority = r.Priority,
                    State = r.State,
                    TitleKey = r.TitleKey,
                    BodyKey = r.BodyKey,
                    BenefitKey = r.BenefitKey,
                    TemplateData = r.TemplateData,
                    CardiMemberId = r.CardiMemberId,
                    CardiMemberName = Lookup(names, r.CardiMemberId),
                    ActionDeepLink = r.ActionDeepLink,
                    CanMute = rule?.Spec.CanMute ?? true,
                    MaxSnoozeHours = (int)(rule?.Spec.MaxSnooze ?? TimeSpan.FromDays(30)).TotalHours,
                    IsOwner = r.IsOwner,
                    SnoozedUntil = r.SnoozedUntil,
                    FirstDetectedDate = r.FirstDetectedDate,
                    FirstSeenDate = r.FirstSeenDate
                };
            })
        ];
    }

    private async Task<Dictionary<Guid, string>> ResolveNamesAsync(IEnumerable<Guid?> memberIds)
    {
        var ids = memberIds.Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToList();
        if (ids.Count == 0)
            return [];

        var members = await _unitOfWork.CardiMembers.FindAsync(m => ids.Contains(m.Id));
        return members.ToDictionary(m => m.Id, m => m.Name);
    }

    private static string? Lookup(Dictionary<Guid, string> names, Guid? id) =>
        id.HasValue && names.TryGetValue(id.Value, out var name) ? name : null;
}
