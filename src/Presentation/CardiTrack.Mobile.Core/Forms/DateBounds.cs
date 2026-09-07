namespace CardiTrack.Mobile.Core.Forms;

/// <summary>
/// The clamping rule behind the app's own date field, kept out of the control so it can be pinned
/// without a MAUI host.
/// </summary>
/// <remarks>
/// A date is held between the field's earliest and latest whichever way it arrives — chosen in the
/// platform dialog, set by the page, or left where it was while a bound moved past it — the same
/// as the platform <c>DatePicker</c> the pages were written against. Only the day is kept: the
/// field shows days, and a time-of-day riding along on a date of birth would make two readings of
/// the same day compare unequal. Nothing is not a date, so null passes through untouched.
/// </remarks>
public static class DateBounds
{
    /// <summary>The earliest day a field allows unless told otherwise; the platform control's own default.</summary>
    public static readonly DateTime DefaultMinimum = new(1900, 1, 1);

    /// <summary>The latest day a field allows unless told otherwise; the platform control's own default.</summary>
    public static readonly DateTime DefaultMaximum = new(2100, 12, 31);

    /// <summary>
    /// Holds <paramref name="value"/> between <paramref name="minimum"/> and <paramref name="maximum"/>,
    /// keeping only its day.
    /// </summary>
    /// <remarks>Earliest before latest, so an earliest set above the latest settles on the latest
    /// rather than throwing while a page is still setting the two.</remarks>
    public static DateTime? Clamp(DateTime? value, DateTime minimum, DateTime maximum)
    {
        if (value is not { } date)
            return null;

        var day = date.Date;
        if (day < minimum.Date)
            day = minimum.Date;
        if (day > maximum.Date)
            day = maximum.Date;
        return day;
    }
}
