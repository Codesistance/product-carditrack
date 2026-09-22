using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Domain.Entities;
using CardiTrack.Mobile.Core.Forms;

namespace CardiTrack.Mobile.Core.Family;

/// <summary>
/// The sentences the family screens say, kept out of the pages so they can be read — and tested —
/// as claims rather than as string literals scattered through code-behind.
/// </summary>
public static class FamilyCopy
{
    /// <summary>D-12, verbatim: the sentence every "Start a family" affordance carries.</summary>
    public const string TrialStartsWithFirstMember =
        "Your free trial starts when you add the first person to watch — not before.";

    /// <summary>§6.3: fan-out copy never names who failed to respond. Kept here so the app never drifts from it.</summary>
    public const string EscalatedNobodyAnswered = "Escalated — nobody has acknowledged this yet";

    /// <summary>The question D-8 forces at acceptance.</summary>
    public const string QuietHoursQuestion =
        "Should an alert nobody else answered wake you during your quiet hours?";

    public static string NightCoverageLine(bool piercesQuietHours) => piercesQuietHours
        ? "An alert nobody else answers will wake you, even in your quiet hours."
        : "An alert nobody else answers waits until your quiet hours end.";

    /// <summary>A pending ask as its card reads: the headline and the line under it.</summary>
    public static (string Title, string Detail) JoinRequestLine(FamilyJoinRequestSummary request, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(request);
        var family = string.IsNullOrWhiteSpace(request.FamilyName) ? "that family" : request.FamilyName;

        return request.Status.ToLowerInvariant() switch
        {
            "pending" => ($"Waiting on {family}",
                $"Asked {RelativeTime.Format(request.RequestedAt)} · their admin has {Remaining(request.ExpiresAt, utcNow)} to answer"),
            "declined" => ($"{family} said no", "You can ask again if that was a mistake."),
            "expired" => ($"Your ask to join {family} ran out",
                "Nobody answered in time. You can ask again."),
            "approved" => ($"You're in {family}", "It should be in your list now."),
            "withdrawn" => ($"You withdrew your ask to join {family}", "You can ask again any time."),
            _ => ($"Your ask to join {family}", request.Status),
        };
    }

    /// <summary>Whether the card should offer "Ask again" rather than "Withdraw".</summary>
    public static bool CanAskAgain(FamilyJoinRequestSummary request) =>
        request.Status.ToLowerInvariant() is "declined" or "expired" or "withdrawn";

    public static bool IsPending(FamilyJoinRequestSummary request) =>
        string.Equals(request.Status, "pending", StringComparison.OrdinalIgnoreCase);

    /// <summary>An invitation's state as the caregiver list shows it.</summary>
    public static string InviteStatusLine(CaregiverInviteResponse invite, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(invite);
        return invite.Status.ToLowerInvariant() switch
        {
            "pending" => $"Not opened yet · works for {Remaining(invite.ExpiresAt, utcNow)}",
            "opened" => $"Opened, not answered yet · works for {Remaining(invite.ExpiresAt, utcNow)}",
            "accepted" => invite.ResolvedAt is { } at ? $"Accepted {RelativeTime.Format(at)}" : "Accepted",
            "declined" => "They said no",
            "revoked" => "Cancelled",
            "expired" => "Ran out before it was used",
            _ => invite.Status,
        };
    }

    public static bool IsLive(CaregiverInviteResponse invite) =>
        invite.Status.ToLowerInvariant() is "pending" or "opened";

    /// <summary>
    /// What "Share Family ID" hands to the share sheet. No link: nothing serves one, and the ID is
    /// the whole of what the recipient needs (D-11).
    /// </summary>
    public static string ShareFamilyIdText(string familyName, string familyId) =>
        $"Join my family on CardiTrack. Open the app, go to the Family tab, and enter this Family ID: "
        + $"{FamilyIdentifier.ToDisplay(familyId)}";

    public static string WatchedLine(IReadOnlyList<string> watchedMemberNames) => watchedMemberNames.Count switch
    {
        0 => "You can't see anyone in this family yet.",
        1 => $"You can see {watchedMemberNames[0]}.",
        2 => $"You can see {watchedMemberNames[0]} and {watchedMemberNames[1]}.",
        _ => $"You can see {string.Join(", ", watchedMemberNames.Take(watchedMemberNames.Count - 1))} and {watchedMemberNames[^1]}.",
    };

    public static string PeopleLine(int count) => count == 1 ? "Just you" : $"{count} people";

    /// <summary>"6 days" / "3 hours" / "less than an hour" until <paramref name="expiresAtUtc"/>; "no time" once past.</summary>
    public static string Remaining(DateTime expiresAtUtc, DateTime utcNow)
    {
        var left = DateTime.SpecifyKind(expiresAtUtc, DateTimeKind.Utc) - utcNow;
        if (left <= TimeSpan.Zero)
            return "no time";
        if (left.TotalHours < 1)
            return "less than an hour";
        if (left.TotalHours < 24)
        {
            var hours = (int)left.TotalHours;
            return hours == 1 ? "1 hour" : $"{hours} hours";
        }

        var days = (int)Math.Ceiling(left.TotalDays);
        return days == 1 ? "1 day" : $"{days} days";
    }
}
