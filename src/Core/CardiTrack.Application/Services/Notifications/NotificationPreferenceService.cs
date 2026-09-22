using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Entities;

namespace CardiTrack.Application.Services.Notifications;

public interface INotificationPreferenceService
{
    /// <summary>Never returns null — a user with no row yet gets the safe fail-closed defaults (§7.1), not a 404.</summary>
    Task<NotificationPreference> GetAsync(Guid userId, CancellationToken ct = default);

    Task<NotificationPreference> UpdateAsync(
        Guid userId,
        TimeOnly? quietHoursStart,
        TimeOnly? quietHoursEnd,
        bool showDetailsOnLockScreen,
        string mutedCategoriesJson,
        bool escalatedAlertsPierceQuietHours = false,
        CancellationToken ct = default);

    /// <summary>
    /// Evaluated against the user's <c>TimeZoneId</c>. A null/unmigrated preference row resolves
    /// to "not in quiet hours" (no deferral) rather than failing — the fail-closed default that
    /// matters here is content-free payloads (§7.1), not quiet-hours enforcement, so an unreadable
    /// preference must never block a Safety/red-Health send.
    /// </summary>
    Task<(bool IsWithinQuietHours, DateTime? EndsAtUtc)> EvaluateQuietHoursAsync(
        Guid userId, string userTimeZoneId, DateTime utcNow, CancellationToken ct = default);

    /// <summary>
    /// Whether this person asked to be woken by an alert escalated to them — one nobody else
    /// answered — inside their own quiet hours.
    /// </summary>
    /// <remarks>
    /// False for a missing preference row, which is the same answer as an explicit no. Unlike the
    /// quiet-hours evaluation above, the conservative default here is to hold rather than to send:
    /// this is a second caregiver being escalated to, and waking somebody who never agreed to be
    /// woken is the failure that teaches a family to mute the app.
    /// </remarks>
    Task<bool> EscalatedAlertsPierceQuietHoursAsync(Guid userId, CancellationToken ct = default);
}

public class NotificationPreferenceService : INotificationPreferenceService
{
    private readonly IUnitOfWork _unitOfWork;

    public NotificationPreferenceService(IUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    public async Task<NotificationPreference> GetAsync(Guid userId, CancellationToken ct = default) =>
        await _unitOfWork.NotificationPreferences.GetByUserIdAsync(userId, ct)
        ?? new NotificationPreference { UserId = userId };

    public async Task<NotificationPreference> UpdateAsync(
        Guid userId,
        TimeOnly? quietHoursStart,
        TimeOnly? quietHoursEnd,
        bool showDetailsOnLockScreen,
        string mutedCategoriesJson,
        bool escalatedAlertsPierceQuietHours = false,
        CancellationToken ct = default)
    {
        var existing = await _unitOfWork.NotificationPreferences.GetByUserIdAsync(userId, ct);
        var entity = existing ?? new NotificationPreference { UserId = userId };

        entity.QuietHoursStart = quietHoursStart;
        entity.QuietHoursEnd = quietHoursEnd;
        entity.ShowDetailsOnLockScreen = showDetailsOnLockScreen;
        entity.EscalatedAlertsPierceQuietHours = escalatedAlertsPierceQuietHours;
        entity.MutedCategories = mutedCategoriesJson;

        if (existing is null)
            await _unitOfWork.NotificationPreferences.AddAsync(entity);
        else
            _unitOfWork.NotificationPreferences.Update(entity);

        await _unitOfWork.SaveChangesAsync();
        return entity;
    }

    public async Task<bool> EscalatedAlertsPierceQuietHoursAsync(
        Guid userId, CancellationToken ct = default)
    {
        var prefs = await _unitOfWork.NotificationPreferences.GetByUserIdAsync(userId, ct);
        return prefs?.EscalatedAlertsPierceQuietHours ?? false;
    }

    public async Task<(bool, DateTime?)> EvaluateQuietHoursAsync(
        Guid userId, string userTimeZoneId, DateTime utcNow, CancellationToken ct = default)
    {
        var prefs = await _unitOfWork.NotificationPreferences.GetByUserIdAsync(userId, ct);
        if (prefs?.QuietHoursStart is null || prefs.QuietHoursEnd is null)
            return (false, null);

        TimeZoneInfo tz;
        try
        {
            tz = TimeZoneInfo.FindSystemTimeZoneById(userTimeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            // An invalid stored zone id must not block a send — fail closed toward "not in
            // quiet hours" the same way an unreadable preference row does.
            return (false, null);
        }

        var localNow = TimeZoneInfo.ConvertTimeFromUtc(utcNow, tz);
        var localTime = TimeOnly.FromDateTime(localNow);
        var start = prefs.QuietHoursStart.Value;
        var end = prefs.QuietHoursEnd.Value;

        if (!QuietHours.Contains(start, end, localTime))
            return (false, null);

        // Same-day window: end falls later today. Overnight window: end falls today if we're
        // already past midnight (localTime < end), tomorrow if we haven't crossed midnight yet
        // (localTime >= start, still "yesterday's" evening segment).
        var endsToday = start <= end || localTime < end;
        var endsLocalDate = endsToday ? localNow.Date : localNow.Date.AddDays(1);
        var endsLocal = endsLocalDate + end.ToTimeSpan();
        var endsUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(endsLocal, DateTimeKind.Unspecified), tz);

        return (true, endsUtc);
    }
}
