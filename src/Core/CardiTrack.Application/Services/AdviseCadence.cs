using CardiTrack.Application.Services.Notifications;

namespace CardiTrack.Application.Services;

/// <summary>
/// Whether Advise is due to regenerate: at most <see cref="MaxPerLocalDay"/> successful writes
/// in the member's local day, spaced through the waking window (the complement of the anchor
/// caregiver's quiet hours). Pure, so the due-gate is unit-testable without a digest pass.
/// </summary>
/// <remarks>
/// The half-hourly digest job still calls the generator on every pass; this is the gate that
/// decides whether that call spends MedGemma. Unset quiet hours mean no night skip and a
/// 24-hour waking window — the same "no deferral" default push uses when the family has not
/// set a window. A <c>PromptVersion</c> bump is due on the next waking pass, not at 03:00.
/// </remarks>
public static class AdviseCadence
{
    /// <summary>Successful regenerations per member local day, spread across waking hours.</summary>
    public const int MaxPerLocalDay = 5;

    /// <summary>
    /// True when a digest pass should spend the clinical + rewrite pair. Callers still apply
    /// the paused/deactivated-member guard first — that is about the member, not the clock.
    /// </summary>
    public static bool IsDue(
        DateTime utcNow,
        DateTime? lastGeneratedAtUtc,
        int storedPromptVersion,
        int currentPromptVersion,
        TimeOnly? quietHoursStart,
        TimeOnly? quietHoursEnd,
        TimeZoneInfo timeZone)
    {
        var now = AsUtc(utcNow);
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(now, timeZone);
        var localTime = TimeOnly.FromDateTime(localNow);

        if (quietHoursStart is { } start && quietHoursEnd is { } end
            && QuietHours.Contains(start, end, localTime))
            return false;

        if (lastGeneratedAtUtc is null || storedPromptVersion < currentPromptVersion)
            return true;

        var lastUtc = AsUtc(lastGeneratedAtUtc.Value);
        var lastLocal = TimeZoneInfo.ConvertTimeFromUtc(lastUtc, timeZone);
        var today = DateOnly.FromDateTime(localNow);
        var lastDay = DateOnly.FromDateTime(lastLocal);

        var waking = quietHoursStart is { } qStart && quietHoursEnd is { } qEnd
            ? TimeSpan.FromDays(1) - QuietHours.Duration(qStart, qEnd)
            : TimeSpan.FromDays(1);
        if (waking <= TimeSpan.Zero)
            return false;

        var slot = waking / MaxPerLocalDay;

        if (lastDay < today)
            return true;

        var candidateUtc = lastUtc + slot;
        if (quietHoursStart is { } nextStart && quietHoursEnd is { } nextEnd)
        {
            var candidateLocal = TimeZoneInfo.ConvertTimeFromUtc(candidateUtc, timeZone);
            if (QuietHours.Contains(nextStart, nextEnd, TimeOnly.FromDateTime(candidateLocal)))
                candidateUtc = NextWakingUtc(candidateLocal, nextStart, nextEnd, timeZone);
        }

        if (now < candidateUtc)
            return false;

        var origin = SlotOrigin(today, quietHoursStart, quietHoursEnd);
        var lastOffset = lastLocal - origin;
        if (lastOffset < TimeSpan.Zero)
            lastOffset = TimeSpan.Zero;

        var slotsUsed = (int)Math.Floor(lastOffset / slot) + 1;
        return slotsUsed < MaxPerLocalDay;
    }

    /// <summary>
    /// The local instant the next slot is counted from on <paramref name="day"/>. Overnight
    /// quiet (start after end) includes midnight, so the waking day opens at quiet-hours end.
    /// A same-day window sits in the middle of the clock: origin stays local midnight so
    /// writes before and after it share one five-slot budget instead of each looking like
    /// the first write of the day.
    /// </summary>
    private static DateTime SlotOrigin(DateOnly day, TimeOnly? quietStart, TimeOnly? quietEnd)
    {
        if (quietStart is { } start && quietEnd is { } end && start > end)
            return day.ToDateTime(end);

        return day.ToDateTime(TimeOnly.MinValue);
    }

    /// <summary>
    /// The UTC instant the family is next awake after <paramref name="localInstant"/>, which
    /// the caller has already placed inside the quiet window. Same-day windows end later
    /// today; overnight windows that have started in the evening end tomorrow.
    /// </summary>
    private static DateTime NextWakingUtc(
        DateTime localInstant, TimeOnly quietStart, TimeOnly quietEnd, TimeZoneInfo timeZone)
    {
        var day = DateOnly.FromDateTime(localInstant);
        var time = TimeOnly.FromDateTime(localInstant);
        var endDay = quietStart > quietEnd && time >= quietStart ? day.AddDays(1) : day;
        return TimeZoneInfo.ConvertTimeToUtc(endDay.ToDateTime(quietEnd), timeZone);
    }

    private static DateTime AsUtc(DateTime value) =>
        value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
