using CardiTrack.Application.DTOs.Responses;

namespace CardiTrack.Mobile.Core.Family;

/// <summary>
/// One family's standing in the switcher drawer: how many of its members' alerts are still open,
/// and the worst of them.
/// </summary>
/// <param name="HighestSeverity">red / orange / yellow, or null when quiet.</param>
public sealed record FamilyAlertSummary(int OpenCount, string? HighestSeverity, DateTime? MostRecentAt)
{
    public static readonly FamilyAlertSummary Quiet = new(0, null, null);

    public bool IsQuiet => OpenCount == 0;
}

/// <summary>
/// Derives the drawer's per-family alert state (D-19) from the open alerts the caregiver can
/// already see, joined to the family by the members' ids.
/// </summary>
/// <remarks>
/// The join is on <c>CardiMemberId</c>: the member list says which family each member belongs to,
/// and every alert names its member. No name is compared anywhere, so two families watching the
/// same person under the same name (D-15) each see only their own record's alerts. A server-side
/// per-family summary would make this class unnecessary; until one exists, this is the one place
/// the derivation lives, with a test.
/// </remarks>
public static class FamilyAlertState
{
    public static FamilyAlertSummary For(
        IReadOnlyCollection<Guid> familyMemberIds, IReadOnlyCollection<AlertSummaryResponse> openAlerts)
    {
        ArgumentNullException.ThrowIfNull(familyMemberIds);
        ArgumentNullException.ThrowIfNull(openAlerts);
        if (familyMemberIds.Count == 0)
            return FamilyAlertSummary.Quiet;

        var members = familyMemberIds as IReadOnlySet<Guid> ?? familyMemberIds.ToHashSet();
        var count = 0;
        string? highest = null;
        DateTime? latest = null;
        foreach (var alert in openAlerts)
        {
            if (!members.Contains(alert.CardiMemberId))
                continue;
            count++;
            highest = HigherOf(highest, alert.Severity);
            if (latest is null || alert.TriggeredAt > latest)
                latest = alert.TriggeredAt;
        }

        return count == 0 ? FamilyAlertSummary.Quiet : new FamilyAlertSummary(count, highest, latest);
    }

    /// <summary>The ids of the members a family owns, from the caller's grant-scoped member list.</summary>
    public static IReadOnlySet<Guid> MembersOf(Guid organizationId, IEnumerable<CardiMemberResponse> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        return members.Where(m => m.OrganizationId == organizationId).Select(m => m.Id).ToHashSet();
    }

    /// <summary>
    /// Families with open alerts first, worst first, and the server's own order otherwise — the
    /// home family leads the rest, which is what a caregiver with two quiet families expects to
    /// see at the top.
    /// </summary>
    public static IReadOnlyList<FamilySummary> OrderForDrawer(
        IReadOnlyList<FamilySummary> families, Func<FamilySummary, FamilyAlertSummary> stateOf)
    {
        ArgumentNullException.ThrowIfNull(families);
        ArgumentNullException.ThrowIfNull(stateOf);

        return families
            .Select((family, index) => (family, index, state: stateOf(family)))
            .OrderByDescending(f => SeverityRank(f.state.HighestSeverity))
            .ThenByDescending(f => f.state.OpenCount)
            .ThenBy(f => f.index)
            .Select(f => f.family)
            .ToList();
    }

    /// <summary>The severity contract's order: red above orange above yellow; anything else is quiet.</summary>
    public static int SeverityRank(string? severity) => severity?.ToLowerInvariant() switch
    {
        "red" => 3,
        "orange" => 2,
        "yellow" => 1,
        _ => 0,
    };

    public static string? HigherOf(string? a, string? b) => SeverityRank(b) > SeverityRank(a) ? b : a;
}
