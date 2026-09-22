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
/// Derives the drawer's per-family alert state (D-19) from the two lists the API does serve.
/// </summary>
/// <remarks>
/// <para>
/// <c>GET /families/mine</c> carries the names of the members a caller may see in each family and
/// nothing that identifies them; the alert list carries a member's id and name. So the join is
/// on the name — trimmed, case-insensitive — which is honest about what the server offers and
/// is the whole reason this lives in one place with a test: when the family summary grows an
/// open-alert count of its own, this class goes and nothing else changes.
/// </para>
/// <para>
/// The known weakness is D-15: two families watching the same person under the same name would
/// each be credited with the other's alerts. That is a display-only overcount in the drawer, and
/// a member's row on the tab still opens only that family's own record.
/// </para>
/// </remarks>
public static class FamilyAlertState
{
    public static FamilyAlertSummary For(FamilySummary family, IReadOnlyCollection<AlertSummaryResponse> openAlerts)
    {
        ArgumentNullException.ThrowIfNull(family);
        ArgumentNullException.ThrowIfNull(openAlerts);

        var watched = new HashSet<string>(
            family.WatchedMemberNames.Select(Normalise).Where(n => n.Length > 0),
            StringComparer.Ordinal);
        if (watched.Count == 0)
            return FamilyAlertSummary.Quiet;

        var count = 0;
        string? highest = null;
        DateTime? latest = null;
        foreach (var alert in openAlerts)
        {
            if (!watched.Contains(Normalise(alert.CardiMemberName)))
                continue;
            count++;
            highest = HigherOf(highest, alert.Severity);
            if (latest is null || alert.TriggeredAt > latest)
                latest = alert.TriggeredAt;
        }

        return count == 0 ? FamilyAlertSummary.Quiet : new FamilyAlertSummary(count, highest, latest);
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

    private static string Normalise(string? name) => (name ?? string.Empty).Trim().ToUpperInvariant();
}
