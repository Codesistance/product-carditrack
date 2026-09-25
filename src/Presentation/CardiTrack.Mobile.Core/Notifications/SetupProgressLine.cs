using CardiTrack.Application.DTOs.Responses;

namespace CardiTrack.Mobile.Core.Notifications;

/// <summary>
/// One member's line in "Complete the picture": a ring filled to how much of their set-up is
/// done, "Pop's profile 3 of 5", and the step to do next.
/// </summary>
/// <param name="Fraction">How much of the ring is filled, 0 to 1.</param>
/// <param name="Title">"Pop's profile 3 of 5".</param>
/// <param name="Next">"Next: emergency contact".</param>
/// <param name="NextStep">The step a tap takes the caregiver to.</param>
public sealed record SetupProgressLine(
    Guid CardiMemberId, double Fraction, string Title, string Next, MemberSetupStep NextStep)
{
    /// <summary>
    /// The set-up reminders the rings stand for. Their own rows leave the card when the rings are
    /// drawn: the same missing emergency contact as a ring's "next" and as a row beside it would
    /// be one gap asked about twice.
    /// </summary>
    public static readonly IReadOnlySet<string> SetupRuleCodes = new HashSet<string>(StringComparer.Ordinal)
    {
        "EMERGENCY_CONTACT_MISSING",
        "MEDICAL_NOTES_EMPTY",
        "MEDICAL_NOTES_STALE",
        "SLEEP_SCOPE_MISSING",
        "IRN_NOT_ENROLLED",
        "TIMEZONE_DEFAULT",
    };

    /// <summary>
    /// The lines worth drawing: members with something left to do that the caller is the one to
    /// do it. A relative sees the same reminders as "somebody else's to fix", so a ring asking
    /// them to act would be the one call to action on the card they cannot answer. Members with
    /// nothing that applies, or everything done, are left out — the card is for what is missing.
    /// </summary>
    public static IReadOnlyList<SetupProgressLine> For(IEnumerable<MemberSetupProgress> members) =>
        members
            .Where(m => m.IsOwner && m.Total > 0 && m.Done < m.Total)
            .Select(m => (Member: m, Next: m.Steps.FirstOrDefault(s => !s.Done)))
            .Where(x => x.Next is not null)
            .Select(x => new SetupProgressLine(
                x.Member.CardiMemberId,
                Math.Clamp((double)x.Member.Done / x.Member.Total, 0, 1),
                $"{Possessive(x.Member.CardiMemberFirstName)} profile {x.Member.Done} of {x.Member.Total}",
                $"Next: {LowerFirst(x.Next!.Title)}",
                x.Next))
            .ToList();

    private static string Possessive(string? name)
    {
        var first = string.IsNullOrWhiteSpace(name) ? "Their" : name.Trim();
        if (first == "Their")
            return first;
        return first.EndsWith('s') ? $"{first}'" : $"{first}'s";
    }

    /// <summary>"Emergency contact" reads as "Next: emergency contact"; an acronym-led title keeps its case.</summary>
    private static string LowerFirst(string title) =>
        title.Length > 1 && char.IsUpper(title[0]) && !char.IsUpper(title[1])
            ? char.ToLowerInvariant(title[0]) + title[1..]
            : title;
}
