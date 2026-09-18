namespace CardiTrack.Application.Services.Notifications;

/// <summary>
/// Overnight and same-day quiet-hour windows, as local wall-clock ranges. Shared so push
/// deferral and Advise's waking cadence cannot disagree about whether 22:00–07:00 contains
/// 23:30.
/// </summary>
public static class QuietHours
{
    /// <summary>
    /// True when <paramref name="localTime"/> sits in <paramref name="start"/>–<paramref name="end"/>.
    /// Overnight windows (start after end, e.g. 22:00–07:00) wrap past midnight; same-day
    /// windows do not. The end instant is exclusive, so 07:00 is already awake.
    /// </summary>
    public static bool Contains(TimeOnly start, TimeOnly end, TimeOnly localTime) =>
        start <= end
            ? localTime >= start && localTime < end
            : localTime >= start || localTime < end;

    /// <summary>How long the window lasts, wrapping overnight when start is after end.</summary>
    public static TimeSpan Duration(TimeOnly start, TimeOnly end) =>
        start <= end
            ? end.ToTimeSpan() - start.ToTimeSpan()
            : TimeSpan.FromDays(1) - start.ToTimeSpan() + end.ToTimeSpan();
}
