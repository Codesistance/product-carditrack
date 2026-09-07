namespace CardiTrack.Mobile.Core.Forms;

/// <summary>
/// The bounds rules behind the app's own date field, kept out of the control so they can be
/// pinned without a MAUI host.
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
    /// The bounds a field actually applies, as days, with an earliest that lies above the latest
    /// collapsed onto the latest.
    /// </summary>
    /// <remarks>
    /// A page sets the two bounds one after the other, and between the two writes they can be
    /// upside down — an earliest of 2031 against a latest still at its 2020 default. The platform
    /// control throws for that; a field that throws mid-form is worse than a field that is wrong
    /// for one statement. So the pair is read as a single day at the latest, which is where
    /// <see cref="Clamp"/> already sends a value under such bounds and where the count stepper
    /// settles its own upside-down bounds. The next write puts the pair right.
    /// </remarks>
    public static (DateTime Minimum, DateTime Maximum) Normalise(DateTime minimum, DateTime maximum)
    {
        var earliest = minimum.Date;
        var latest = maximum.Date;
        return earliest > latest ? (latest, latest) : (earliest, latest);
    }

    /// <summary>
    /// Whether the latest has to be handed to the platform control before the earliest. The
    /// control refuses an earliest above the latest it currently holds, so when the new earliest
    /// is past the old latest, the latest must move first.
    /// </summary>
    /// <param name="effectiveMinimum">The earliest about to be applied — already normalised.</param>
    /// <param name="currentMaximum">The latest the platform control holds now.</param>
    public static bool LatestFirst(DateTime effectiveMinimum, DateTime currentMaximum) =>
        effectiveMinimum.Date > currentMaximum.Date;

    /// <summary>
    /// Holds <paramref name="value"/> between <paramref name="minimum"/> and <paramref name="maximum"/>,
    /// keeping only its day.
    /// </summary>
    /// <remarks>Bounds are read through <see cref="Normalise"/>, so an earliest set above the
    /// latest settles a value on the latest rather than throwing while a page is still setting the two.</remarks>
    public static DateTime? Clamp(DateTime? value, DateTime minimum, DateTime maximum)
    {
        if (value is not { } date)
            return null;

        var (earliest, latest) = Normalise(minimum, maximum);
        var day = date.Date;
        if (day < earliest)
            day = earliest;
        if (day > latest)
            day = latest;
        return day;
    }
}
