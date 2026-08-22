using CardiTrack.Application.DTOs.Responses;

namespace CardiTrack.Mobile.Core.Alerts;

/// <summary>
/// What the dashboard says when there has been nothing to say for a while — the copy behind
/// <see cref="DashboardResponse.Reassurance"/>.
/// </summary>
/// <remarks>
/// <para>
/// Lives here rather than in the page for the same reason <see cref="AlertRuleOrder"/> does: it is
/// the wording a family reads about their relative being fine, and wording that carries that much
/// is worth a test. The server sends the numbers and this turns them into the sentence, so the
/// duration and the claim can never drift apart the way a server-composed string and a
/// client-composed one eventually do.
/// </para>
/// <para>
/// The voice is deliberately flat. "Everything is perfect" is a promise no monitoring product can
/// keep, and a caregiver who reads it once and is then called about a fall stops believing the
/// rest of the screen. What is true is narrower and still worth hearing: we have been watching,
/// and nothing has come up.
/// </para>
/// </remarks>
public static class ReassuranceCopy
{
    /// <summary>The headline — the whole message in two words for someone who reads no further.</summary>
    public const string Title = "All quiet";

    /// <summary>
    /// The sentence beneath it. Names the person, the stretch, and — the part that makes it
    /// reassurance rather than an empty state — that the watch is still running.
    /// </summary>
    /// <param name="firstName">
    /// The member's first name, or blank. A blank one falls back to "them": every other
    /// possibility ("this CardiMember") reads like a database row in a sentence whose entire job
    /// is to sound like a person telling you your mother is fine.
    /// </param>
    public static string Detail(ReassuranceResponse reassurance, string? firstName)
    {
        var who = string.IsNullOrWhiteSpace(firstName) ? "them" : firstName;
        var stretch = Stretch(reassurance.QuietDays);

        // A member who has never had an alert has no episode to be "since" — saying "no alerts
        // for 40 days" would invite the reading that one is now overdue. "Nothing has come up
        // since we started watching" is the same fact without the implied clock.
        return reassurance.FollowsAnAlert
            ? $"Nothing has come up for {who} in {stretch}. CardiTrack is still watching."
            : $"Nothing has come up for {who} in the {stretch} we've been watching.";
    }

    /// <summary>
    /// The stretch in the largest unit that stays honest. Weeks past a fortnight, because "in 34
    /// days" is a number a reader has to convert and "in 4 weeks" is one they feel; days below
    /// that, because "in 1 week" for eight days rounds away a day the caregiver has counted.
    /// </summary>
    private static string Stretch(int quietDays)
    {
        if (quietDays < 14)
            return quietDays == 1 ? "a day" : $"{quietDays} days";

        var weeks = quietDays / 7;
        return $"{weeks} weeks";
    }
}
