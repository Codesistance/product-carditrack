using CardiTrack.Application.DTOs.Responses;

namespace CardiTrack.Mobile.Core.Devices;

/// <summary>
/// The words and choices behind the M1-15 "Re-pull History" action: which ranges a caregiver
/// can pick, and how a request's state reads on the device card. Pure, so the copy is testable
/// without a page.
/// </summary>
public static class HistoryRepullCopy
{
    /// <summary>The ranges offered, in the order the picker lists them.</summary>
    public static readonly IReadOnlyList<int> DayChoices = [7, 14, 30, 45, 60, 75, 90];

    /// <summary>
    /// How many days a Worker tick fetches, and how often it ticks — what the wait estimate is
    /// built from. Mirrors the server's chunk size and cron; an estimate, not a promise.
    /// </summary>
    private const int DaysPerTick = 7;
    private const int MinutesPerTick = 10;

    public static string Label(int days) => $"Last {days} days";

    public static string[] ChoiceLabels() => DayChoices.Select(Label).ToArray();

    /// <summary>The day count behind a picker label, or null for anything else.</summary>
    public static int? DaysFor(string? label) =>
        DayChoices.Cast<int?>().FirstOrDefault(d => Label(d!.Value) == label);

    /// <summary>"about 10 minutes", "about an hour", "about 2 hours" — how long a request takes.</summary>
    public static string Estimate(int days)
    {
        var ticks = (days + DaysPerTick - 1) / DaysPerTick;
        var minutes = ticks * MinutesPerTick;
        return minutes switch
        {
            < 55 => $"about {minutes} minutes",
            < 90 => "about an hour",
            _ => $"about {(minutes + 30) / 60} hours",
        };
    }

    /// <summary>
    /// The line under the action, or null when there is nothing to say. Never carries a failure
    /// reason — that is a type name for the logs, not something a caregiver acts on.
    /// </summary>
    public static string? StatusLine(DeviceHistoryRepullResponse? repull, DateTime utcNow)
    {
        if (repull is null)
            return null;

        return repull.Status switch
        {
            "pending" => "Queued — starts within 10 minutes",
            "in_progress" => $"Re-pulling last {repull.Days} days · {repull.DaysDone} of {repull.Days} days done",
            "completed" => AvailableAgain(repull, utcNow) is { } wait
                ? $"Done — {repull.DaysWithData} of {repull.Days} days had data · available again {wait}"
                : $"Done — {repull.DaysWithData} of {repull.Days} days had data",
            "failed" => "Didn't finish — you can try again",
            "cancelled" => "Stopped — monitoring paused or the device changed",
            _ => null,
        };
    }

    /// <summary>Whether the action should be offered: nothing open, and no cooldown in force.</summary>
    public static bool CanRequest(DeviceHistoryRepullResponse? repull, DateTime utcNow)
    {
        if (repull is null)
            return true;

        if (repull.Status is "pending" or "in_progress")
            return false;

        return repull.NextAllowedAt is not { } next || next <= utcNow;
    }

    private static string? AvailableAgain(DeviceHistoryRepullResponse repull, DateTime utcNow)
    {
        if (repull.NextAllowedAt is not { } next || next <= utcNow)
            return null;

        // Hours up to two days: with a 48-hour cooldown nearly every wait is under that, and
        // "about 30 hours" tells a caregiver more than "about 2 days" would. Inclusive of 48
        // because the hours are rounded up — a 47-and-a-bit-hour wait becomes 48, and an
        // exclusive bound would send exactly the freshest cooldown to the vaguest wording.
        // Days are rounded rather than ceilinged: 49 hours is about two days, not three.
        var wait = next - utcNow;
        var hours = (int)Math.Ceiling(wait.TotalHours);
        return hours switch
        {
            <= 1 => "in about an hour",
            <= 48 => $"in about {hours} hours",
            _ => $"in about {(int)Math.Round(wait.TotalDays, MidpointRounding.AwayFromZero)} days",
        };
    }
}
