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

        if (quietHoursStart is { } start && quietHoursEnd is { } end
            && QuietHours.Contains(start, end, TimeOnly.FromDateTime(localNow)))
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

        var lastWaking = WakingElapsedSinceMidnight(
            lastUtc, lastLocal, today, timeZone, quietHoursStart, quietHoursEnd);
        var nextWaking = lastWaking + slot;
        if (nextWaking >= waking)
            return false;

        var candidateLocal = today.ToDateTime(
            WallClockFromWaking(nextWaking, quietHoursStart, quietHoursEnd));
        var candidateUtc = LocalToUtc(candidateLocal, timeZone);
        if (now < candidateUtc)
            return false;

        var slotsUsed = (int)Math.Floor(lastWaking / slot) + 1;
        return slotsUsed < MaxPerLocalDay;
    }

    /// <summary>
    /// Waking time from local midnight to <paramref name="lastLocal"/>, as UTC elapsed minus
    /// quiet hours already passed. UTC elapsed keeps the repeated hour on a fall-back day
    /// instead of counting <c>TimeOfDay</c> twice as the same clock face.
    /// </summary>
    private static TimeSpan WakingElapsedSinceMidnight(
        DateTime lastUtc,
        DateTime lastLocal,
        DateOnly today,
        TimeZoneInfo timeZone,
        TimeOnly? quietStart,
        TimeOnly? quietEnd)
    {
        var originUtc = LocalToUtc(today.ToDateTime(TimeOnly.MinValue), timeZone);
        var elapsed = lastUtc - originUtc;
        if (elapsed < TimeSpan.Zero)
            elapsed = TimeSpan.Zero;

        if (quietStart is not { } start || quietEnd is not { } end)
            return elapsed;

        var waking = elapsed - QuietElapsedSinceMidnight(start, end, TimeOnly.FromDateTime(lastLocal));
        return waking < TimeSpan.Zero ? TimeSpan.Zero : waking;
    }

    /// <summary>
    /// Quiet time from local midnight to <paramref name="at"/>, wrapping overnight when start
    /// is after end. Same-day windows contribute nothing until <paramref name="at"/> reaches
    /// start, then their full duration once it has passed end.
    /// </summary>
    private static TimeSpan QuietElapsedSinceMidnight(TimeOnly start, TimeOnly end, TimeOnly at)
    {
        if (start <= end)
        {
            if (at <= start)
                return TimeSpan.Zero;
            if (at >= end)
                return QuietHours.Duration(start, end);
            return at.ToTimeSpan() - start.ToTimeSpan();
        }

        var morning = at < end ? at.ToTimeSpan() : end.ToTimeSpan();
        var evening = at <= start ? TimeSpan.Zero : at.ToTimeSpan() - start.ToTimeSpan();
        return morning + evening;
    }

    /// <summary>
    /// The wall-clock time at which <paramref name="wakingOffset"/> waking hours have elapsed
    /// since local midnight, jumping over the quiet window.
    /// </summary>
    private static TimeOnly WallClockFromWaking(
        TimeSpan wakingOffset, TimeOnly? quietStart, TimeOnly? quietEnd)
    {
        if (quietStart is not { } start || quietEnd is not { } end)
            return TimeOnly.FromTimeSpan(ClampToDay(wakingOffset));

        if (start <= end)
        {
            var morning = start.ToTimeSpan();
            if (wakingOffset < morning)
                return TimeOnly.FromTimeSpan(ClampToDay(wakingOffset));
            return end.Add(wakingOffset - morning);
        }

        return end.Add(wakingOffset);
    }

    private static TimeSpan ClampToDay(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
            return TimeSpan.Zero;
        var day = TimeSpan.FromDays(1);
        return value >= day ? day - TimeSpan.FromTicks(1) : value;
    }

    /// <summary>
    /// Local unspecified time as UTC. A spring-forward can delete the configured instant
    /// (quiet-hours end at 02:30, a slot at 02:00); the first minute that exists is used so
    /// the digest cannot throw and leave the candidate blocked for the rest of the day.
    /// </summary>
    private static DateTime LocalToUtc(DateTime local, TimeZoneInfo timeZone)
    {
        var value = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        while (timeZone.IsInvalidTime(value))
            value = value.AddMinutes(1);
        return TimeZoneInfo.ConvertTimeToUtc(value, timeZone);
    }

    private static DateTime AsUtc(DateTime value) =>
        value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
