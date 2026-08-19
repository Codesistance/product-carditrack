namespace CardiTrack.Application.DTOs.Requests;

/// <summary>
/// When this member's CardiJournal books are written, in the member's own local time.
/// </summary>
/// <remarks>
/// Its own request rather than four more fields on <see cref="UpdateCardiMemberRequest"/>, which
/// is a full-replacement form. There, an omitted field means "clear it", and null here has to
/// mean "use the default" — the same two meanings that already cost this codebase a silent
/// regression on <c>Gender</c> (see that form's remarks). Kept apart, null means one thing.
/// </remarks>
public class UpdateJournalSettingsRequest
{
    /// <summary>Local time the Daybook is written. Null restores the default.</summary>
    public TimeOnly? DaybookLocalTime { get; set; }

    /// <summary>Local time the Weekbook is written, on <see cref="WeekStartsOn"/>. Null restores the default.</summary>
    public TimeOnly? WeekbookLocalTime { get; set; }

    /// <summary>Local time the Monthbook is written, on the 1st. Null restores the default.</summary>
    public TimeOnly? MonthbookLocalTime { get; set; }

    /// <summary>The weekday a journal week begins. Null restores the default (Monday).</summary>
    public DayOfWeek? WeekStartsOn { get; set; }
}
