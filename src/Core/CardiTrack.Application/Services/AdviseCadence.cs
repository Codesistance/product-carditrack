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

        TimeOnly? quietStart = quietHoursStart;
        TimeOnly? quietEnd = quietHoursEnd;
        if (quietStart is { } start && quietEnd is { } end)
        {
            if (QuietHours.Contains(start, end, localTime))
                return false;
        }

        if (lastGeneratedAtUtc is null || storedPromptVersion < currentPromptVersion)
            return true;

        var lastUtc = AsUtc(lastGeneratedAtUtc.Value);
        var lastLocal = TimeZoneInfo.ConvertTimeFromUtc(lastUtc, timeZone);
        var today = DateOnly.FromDateTime(localNow);
        var lastDay = DateOnly.FromDateTime(lastLocal);

        var waking = quietStart is { } qStart && quietEnd is { } qEnd
            ? TimeSpan.FromDays(1) - QuietHours.Duration(qStart, qEnd)
            : TimeSpan.FromDays(1);
        if (waking <= TimeSpan.Zero)
            return false;

        var slot = waking / MaxPerLocalDay;

        if (lastDay < today)
            return true;

        if (now < lastUtc + slot)
            return false;

        var nextLocal = TimeZoneInfo.ConvertTimeFromUtc(lastUtc + slot, timeZone);
        if (quietStart is { } nextStart && quietEnd is { } nextEnd
            && QuietHours.Contains(nextStart, nextEnd, TimeOnly.FromDateTime(nextLocal)))
            return false;

        var wakingStart = WakingStart(today, quietStart, quietEnd);
        var lastOffset = lastLocal - wakingStart;
        if (lastOffset < TimeSpan.Zero)
            lastOffset = TimeSpan.Zero;

        var slotsUsed = (int)Math.Floor(lastOffset / slot) + 1;
        return slotsUsed < MaxPerLocalDay;
    }

    /// <summary>
    /// The local instant the waking window opens on <paramref name="day"/>: quiet-hours end
    /// when a window is set, otherwise local midnight.
    /// </summary>
    private static DateTime WakingStart(DateOnly day, TimeOnly? quietStart, TimeOnly? quietEnd)
    {
        if (quietStart is not null && quietEnd is { } end)
            return day.ToDateTime(end);

        return day.ToDateTime(TimeOnly.MinValue);
    }

    private static DateTime AsUtc(DateTime value) =>
        value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
