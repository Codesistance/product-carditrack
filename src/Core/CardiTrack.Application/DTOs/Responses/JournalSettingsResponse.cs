namespace CardiTrack.Application.DTOs.Responses;

/// <summary>
/// When this member's CardiJournal books are written, and the bounds a client may offer.
/// </summary>
/// <remarks>
/// Both the chosen value and the effective one are returned. A client needs the chosen value to
/// show a control as "not set" rather than as the default explicitly picked, and the effective
/// value to say when the next book actually lands — inferring one from the other would put the
/// default in two places and let them drift.
/// </remarks>
public class JournalSettingsResponse
{
    public Guid CardiMemberId { get; set; }

    /// <summary>Null when the caregiver has never chosen one.</summary>
    public TimeOnly? DaybookLocalTime { get; set; }
    public TimeOnly? WeekbookLocalTime { get; set; }
    public TimeOnly? MonthbookLocalTime { get; set; }
    public DayOfWeek? WeekStartsOn { get; set; }

    /// <summary>What the generator will actually use, chosen or defaulted.</summary>
    public TimeOnly EffectiveDaybookLocalTime { get; set; }
    public TimeOnly EffectiveWeekbookLocalTime { get; set; }
    public TimeOnly EffectiveMonthbookLocalTime { get; set; }
    public DayOfWeek EffectiveWeekStartsOn { get; set; }

    /// <summary>The member's own timezone — the clock all of the above are read against.</summary>
    public string TimeZoneId { get; set; } = string.Empty;

    /// <summary>Bounds and granularity a client must offer, so it cannot present an unhonourable time.</summary>
    public TimeOnly EarliestSelectableTime { get; set; }
    public TimeOnly LatestSelectableTime { get; set; }
    public int StepMinutes { get; set; }

    /// <summary>
    /// How far a reading has to sit from the member's own usual before a book names a direction
    /// for it. Null where the caregiver has never chosen one.
    /// </summary>
    public int? BedtimeToleranceMinutes { get; set; }
    public int? WakeToleranceMinutes { get; set; }
    public int? DirectionBoundMinutes { get; set; }
    public decimal? LevelTolerancePercent { get; set; }

    /// <summary>What the books will actually use, chosen or defaulted.</summary>
    public int EffectiveBedtimeToleranceMinutes { get; set; }
    public int EffectiveWakeToleranceMinutes { get; set; }
    public int EffectiveDirectionBoundMinutes { get; set; }
    public decimal EffectiveLevelTolerancePercent { get; set; }

    /// <summary>The bounds a client must keep the four above inside.</summary>
    public int MaximumToleranceMinutes { get; set; }
    public int MinimumDirectionBoundMinutes { get; set; }
    public int MaximumDirectionBoundMinutes { get; set; }
    public decimal MaximumLevelTolerancePercent { get; set; }

    /// <summary>
    /// False while a book's generator does not exist yet (Weekbook and Monthbook, R2). The
    /// setting stores and returns fine; a client shows it as coming rather than as live, so the
    /// app never implies a book is being written when nothing writes it.
    /// </summary>
    public bool WeekbookAvailable { get; set; }
    public bool MonthbookAvailable { get; set; }
}
