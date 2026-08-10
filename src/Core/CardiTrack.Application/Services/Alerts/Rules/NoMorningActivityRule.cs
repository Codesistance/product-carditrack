using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Services.Alerts.Rules;

/// <summary>
/// The member's watch is reporting today, and it is reporting almost no movement well into the
/// morning. The one rule that means "go and check on them".
/// </summary>
/// <remarks>
/// <para>
/// This is the only red-severity rule, so it is the one most worth getting wrong-way-round:
/// <b>silence in the data must never be read as silence in the person.</b> It fires only when
/// today's hourly series exists and shows movement is absent — never when the series is missing,
/// which means the watch is flat, off, or out of range. A device that has stopped syncing is a
/// different problem with its own notification.
/// </para>
/// <para>
/// It is also the only rule that needs a local clock. A caregiver still on the <c>"UTC"</c> default
/// would have "by 11am" evaluated against the wrong day part — 3am in Los Angeles — so the rule
/// stays silent until a real zone is set, and the timezone nudge asks for one.
/// </para>
/// </remarks>
public sealed class NoMorningActivityRule : IAlertRule
{
    /// <summary>The hour by which some movement is expected, in the member's local time.</summary>
    public const int ExpectedByLocalHour = 11;

    /// <summary>
    /// Steps below this over the whole morning count as no movement. Not zero: a watch on a
    /// bedside table registers a few counts from being picked up.
    /// </summary>
    public const int MovementFloorSteps = 100;

    /// <summary>
    /// Hours of the morning that must be present in the series before absence means anything.
    /// A watch that synced at 6am and nothing since tells us about the watch, not the wearer.
    /// </summary>
    public const int RequiredReportedHours = 3;

    public AlertType AlertType => AlertType.PatternBreak;
    public AlertSeverity Severity => AlertSeverity.Red;

    public AlertVerdict Evaluate(AlertContext context)
    {
        if (string.Equals(context.TimeZoneId, "UTC", StringComparison.OrdinalIgnoreCase))
            return AlertVerdict.None;

        if (!TryGetLocalNow(context, out var localNow))
            return AlertVerdict.None;

        // Too early to conclude anything.
        if (localNow.Hour < ExpectedByLocalHour)
            return AlertVerdict.None;

        var morning = context.TodayHours
            .Where(h => h.LocalHour < ExpectedByLocalHour)
            .ToList();

        // No series at all, or too little of it: the device is not reporting, which is a device
        // problem. Raising a red alert here would wake a family because a watch needed charging.
        if (morning.Count < RequiredReportedHours)
            return AlertVerdict.None;

        var steps = morning.Sum(h => h.Steps);
        if (steps >= MovementFloorSteps)
            return AlertVerdict.None;

        return AlertVerdict.Raise(
            "No movement this morning",
            $"{ExpectedByLocalHour}am and the watch has recorded almost no movement today, "
            + "though it is still reporting normally. Please check on them.",
            new Dictionary<string, object>
            {
                ["morningSteps"] = steps,
                ["reportedHours"] = morning.Count,
                ["byLocalHour"] = ExpectedByLocalHour
            });
    }

    private static bool TryGetLocalNow(AlertContext context, out DateTime localNow)
    {
        localNow = default;

        // An unrecognised zone must not be silently treated as UTC — that is precisely the
        // mistake this rule guards against everywhere else.
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(context.TimeZoneId, out var zone))
            return false;

        localNow = TimeZoneInfo.ConvertTimeFromUtc(context.UtcNow, zone);
        return true;
    }
}
