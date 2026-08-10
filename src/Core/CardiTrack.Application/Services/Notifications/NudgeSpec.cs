using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Services.Notifications;

/// <summary>How a rule behaves once it fires — the parts that never depend on the data.</summary>
public sealed record NudgeSpec
{
    public required NotificationCategory Category { get; init; }
    public required NotificationPriority Priority { get; init; }

    /// <summary>How long a snooze lasts by default when the user does not choose.</summary>
    public required TimeSpan DefaultSnooze { get; init; }

    /// <summary>
    /// Longest snooze the user may pick. Safety rules cap at 72 hours: silencing "monitoring is
    /// down" for a month is not a preference we can honour.
    /// </summary>
    public required TimeSpan MaxSnooze { get; init; }

    /// <summary>
    /// False for Safety rules. They cannot be muted; dismissing one instead records an
    /// acknowledged consequence and an audit entry.
    /// </summary>
    public bool CanMute { get; init; } = true;

    /// <summary>
    /// Evaluate even while the member's monitoring is paused. True only for rules that are
    /// <em>about</em> the pause — everything else must go quiet when a caregiver deliberately
    /// pauses someone.
    /// </summary>
    public bool AppliesWhenPaused { get; init; }

    /// <summary>
    /// Evaluate inside the 48-hour new-account grace period. True for Safety rules and for the
    /// lost-device case, which is exactly the thing a brand-new account needs told about.
    /// </summary>
    public bool AppliesDuringGracePeriod { get; init; }

    /// <summary>
    /// Evaluate even while an unacknowledged red alert is open for the member. True for Safety
    /// rules, since "we cannot see them any more" is not housekeeping.
    /// </summary>
    public bool AppliesDuringRedAlert { get; init; }

    /// <summary>A Safety-class spec with the caps and exemptions that class implies.</summary>
    public static NudgeSpec Safety(NotificationPriority priority = NotificationPriority.Critical) => new()
    {
        Category = NotificationCategory.Safety,
        Priority = priority,
        DefaultSnooze = TimeSpan.FromHours(24),
        MaxSnooze = TimeSpan.FromHours(72),
        CanMute = false,
        AppliesDuringGracePeriod = true,
        AppliesDuringRedAlert = true
    };
}
