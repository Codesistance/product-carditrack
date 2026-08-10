using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Services.Alerts;

/// <summary>
/// Everything an alert rule is allowed to see about one CardiMember, fetched once before any rule
/// runs.
/// </summary>
/// <remarks>
/// Pure inputs, exactly like <see cref="Notifications.NudgeContext"/>: no repositories, no clock.
/// A statistical rule that decides whether to wake a family at 3am is worth being able to test at
/// its boundaries without a database.
/// </remarks>
public sealed record AlertContext
{
    public required DateTime UtcNow { get; init; }
    public required Guid CardiMemberId { get; init; }

    /// <summary>How far a metric must drift before this family hears about it.</summary>
    public required AlertSensitivity Sensitivity { get; init; }

    /// <summary>
    /// The clock "this morning" is judged against — the primary caregiver's, since a CardiMember
    /// has no time zone of their own. Left as <c>"UTC"</c> when the caregiver never set one, which
    /// suppresses the time-of-day rule rather than judging a Los Angeles morning at 3am local.
    /// </summary>
    public required string TimeZoneId { get; init; }

    /// <summary>
    /// The established baseline, or null while the member is still learning. Null means no rule
    /// fires: a comparison against a half-formed picture of "normal" is a false alarm waiting to
    /// happen, and the first thing a new caregiver experiences must not be one.
    /// </summary>
    public AlertBaselineSnapshot? Baseline { get; init; }

    /// <summary>Recent whole days, oldest first, excluding today.</summary>
    public IReadOnlyList<AlertDaySnapshot> RecentDays { get; init; } = [];

    /// <summary>
    /// Hourly step totals for the member's local today, oldest first. Empty when no sub-daily data
    /// exists — which must never be read as "they haven't moved".
    /// </summary>
    public IReadOnlyList<AlertHourSnapshot> TodayHours { get; init; } = [];

    /// <summary>Alert types already open for this member, so a condition is raised once, not daily.</summary>
    public IReadOnlyList<AlertType> OpenAlertTypes { get; init; } = [];

    public DateTime? MonitoringPausedUntil { get; init; }

    public bool IsMonitoringPaused => MonitoringPausedUntil > UtcNow;

    /// <summary>
    /// Multiplier on how far a metric must drift before a rule fires. High sensitivity trips
    /// earlier, Low needs a clearer signal — this is the only thing <see cref="AlertSensitivity"/>
    /// does, and until now it did nothing at all.
    /// </summary>
    public decimal SensitivityFactor => Sensitivity switch
    {
        AlertSensitivity.High => 0.75m,
        AlertSensitivity.Low => 1.25m,
        _ => 1.0m
    };
}

public sealed record AlertBaselineSnapshot
{
    public required int PeriodDays { get; init; }

    // Each is null when the baseline calculator had fewer than seven samples of that metric —
    // a member can have steps every day and sleep on half of them, so rules gate per metric.
    public int? AvgSteps { get; init; }
    public decimal? StdDevSteps { get; init; }
    public int? AvgRestingHeartRate { get; init; }
    public decimal? StdDevHeartRate { get; init; }
    public int? AvgSleepMinutes { get; init; }
    public int? AvgSleepEfficiency { get; init; }
}

public sealed record AlertDaySnapshot
{
    public required DateOnly Date { get; init; }
    public int? Steps { get; init; }
    public int? RestingHeartRate { get; init; }
    public int? SleepEfficiency { get; init; }
    public int? SleepMinutes { get; init; }
    public int? ActiveMinutes { get; init; }
}

public sealed record AlertHourSnapshot
{
    /// <summary>Hour of the member's local day, 0–23.</summary>
    public required int LocalHour { get; init; }

    public required int Steps { get; init; }
}
