using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;

namespace CardiTrack.Mobile.Core.Journal;

/// <summary>
/// The journal settings screen's pending state: every value the PUT replaces, held as the
/// caregiver's own choice rather than as what those choices resolve to.
/// </summary>
/// <remarks>
/// <para>
/// The PUT is a full replacement of every value on that screen, so each save resends the ones the
/// caregiver did not touch. Two things follow, and the record shape carries both.
/// </para>
/// <para>
/// <b>Every field, or a save clears one.</b> Null on the request means "back to the default", so a
/// draft holding only the book timings would clear a caregiver's bedtime tolerance the next time
/// they moved their Daybook — a setting lost to a save about something else, with nothing on
/// screen to show it had gone.
/// </para>
/// <para>
/// <b>Nullable, or a save pins one.</b> The values are the <em>chosen</em> ones the response
/// carries, not the effective ones. Resending an effective default writes it back as an explicit
/// choice: the member stops being unset, and stops following the default if it ever moves. The
/// response returns both halves precisely so a client can tell "not set" from "the default,
/// picked" — reading the effective half here would throw that distinction away on the first save
/// of anything.
/// </para>
/// <para>
/// In Core rather than in the page for the reason <see cref="JournalComparisonChoices"/> gives:
/// the MAUI project cannot be unit tested, and which values a save carries is exactly the kind of
/// thing that fails silently and is worth pinning.
/// </para>
/// </remarks>
public readonly record struct JournalSettingsDraft(
    TimeOnly? DaybookLocalTime,
    TimeOnly? WeekbookLocalTime,
    TimeOnly? MonthbookLocalTime,
    DayOfWeek? WeekStartsOn,
    int? BedtimeToleranceMinutes,
    int? WakeToleranceMinutes,
    int? DirectionBoundMinutes,
    decimal? LevelTolerancePercent)
{
    /// <summary>
    /// The settings as they stand, ready for one field to be replaced with <c>with</c>. Read from
    /// the last response rather than from the screen's labels — a label is formatted text, and
    /// parsing it back would be a second source of truth.
    /// </summary>
    public static JournalSettingsDraft From(JournalSettingsResponse settings) =>
        new(settings.DaybookLocalTime,
            settings.WeekbookLocalTime,
            settings.MonthbookLocalTime,
            settings.WeekStartsOn,
            settings.BedtimeToleranceMinutes,
            settings.WakeToleranceMinutes,
            settings.DirectionBoundMinutes,
            settings.LevelTolerancePercent);

    public UpdateJournalSettingsRequest ToRequest() => new()
    {
        DaybookLocalTime = DaybookLocalTime,
        WeekbookLocalTime = WeekbookLocalTime,
        MonthbookLocalTime = MonthbookLocalTime,
        WeekStartsOn = WeekStartsOn,
        BedtimeToleranceMinutes = BedtimeToleranceMinutes,
        WakeToleranceMinutes = WakeToleranceMinutes,
        DirectionBoundMinutes = DirectionBoundMinutes,
        LevelTolerancePercent = LevelTolerancePercent,
    };
}
