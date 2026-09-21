namespace CardiTrack.Mobile.Core.Forms;

/// <summary>
/// How long ago something happened, in words.
/// </summary>
/// <remarks>
/// Moved out of the MAUI project when it grew a month-and-year tail: nothing here touches a
/// control, the branching is where the mistakes live, and the test project cannot reference the
/// MAUI assembly — the same "keep it in the testable half" line <c>NudgeLinkParser</c> draws.
/// </remarks>
public static class RelativeTime
{
    /// <summary>
    /// "just now", "10 minutes ago", "2 hours ago", "3 days ago", "5 months ago", "2 years ago".
    /// </summary>
    /// <remarks>
    /// The unit coarsens as the span grows, because precision stops being information: "412 days
    /// ago" is an arithmetic problem, and nobody reading it wanted the number of days. Everything
    /// under two months is unchanged from when this only counted days — the surfaces that ask
    /// about a sync or an alert live there — and the longer tail exists for the ones that can
    /// legitimately be old: a health background nobody has confirmed, a dormant device, an alert
    /// pulled out of history.
    /// </remarks>
    public static string Format(DateTime utcTimestamp)
    {
        var elapsed = DateTime.UtcNow - DateTime.SpecifyKind(utcTimestamp, DateTimeKind.Utc);
        if (elapsed < TimeSpan.Zero)
            elapsed = TimeSpan.Zero;

        return elapsed.TotalMinutes switch
        {
            < 1 => "just now",
            < 2 => "1 minute ago",
            < 60 => $"{(int)elapsed.TotalMinutes} minutes ago",
            < 120 => "1 hour ago",
            < 24 * 60 => $"{(int)elapsed.TotalHours} hours ago",
            < 48 * 60 => "1 day ago",
            < 60 * 24 * 60 => $"{(int)elapsed.TotalDays} days ago",
            < DaysPerYear * 24 * 60 => Months(elapsed),
            _ => Years(elapsed),
        };
    }

    /// <summary>
    /// Nominal lengths, not calendar ones. Nothing here is doing date arithmetic — it is choosing
    /// a word — and a leap-year-accurate 365.25 puts two years at 730.5 days, so something two
    /// days short of its second anniversary renders as one year. Whole days divide cleanly.
    /// </summary>
    private const double DaysPerMonth = 30.44;
    private const int DaysPerYear = 365;

    private static string Months(TimeSpan elapsed)
    {
        var months = (int)(elapsed.TotalDays / DaysPerMonth);
        // Two months of days divides to 1 at an average month length, and "1 month ago" for
        // something nine weeks old is wrong in the direction that matters here — it reads as more
        // recent than it is.
        return months <= 1 ? "1 month ago" : $"{months} months ago";
    }

    private static string Years(TimeSpan elapsed)
    {
        var years = (int)(elapsed.TotalDays / DaysPerYear);
        return years <= 1 ? "1 year ago" : $"{years} years ago";
    }
}
