namespace CardiTrack.Mobile.Core.Members;

/// <summary>
/// The order the dashboard stacks its CardiMember cards in: the ones the caregiver pinned first,
/// then the rest by how much they need looking at, then by first name.
/// </summary>
/// <remarks>
/// Status before name because the dashboard is where a caregiver checks on several people at once,
/// and the one who needs them should not be the third card down because of the alphabet. Pins
/// come before status because the caregiver sometimes knows better — the parent who lives alone
/// stays on top whatever the readings say.
/// </remarks>
public static class MemberCardOrder
{
    /// <summary>
    /// How far up a <c>DashboardResponse.HealthStatus</c> puts its card: red first, then orange and
    /// yellow; then a member we cannot read yet (no data, or still learning) — a gap is worth more
    /// attention than a member who is fine; then green; then a member whose monitoring is paused,
    /// who is on purpose not being watched. Anything unrecognised is treated as unknown.
    /// </summary>
    public static int StatusRank(string? healthStatus) => healthStatus switch
    {
        "red" => 0,
        "orange" => 1,
        "yellow" => 2,
        "green" => 4,
        "paused" => 5,
        _ => 3,
    };

    /// <summary>The member ids in dashboard order.</summary>
    public static IReadOnlyList<Guid> Order(
        IEnumerable<(Guid Id, string? HealthStatus, string FirstName)> members, IReadOnlySet<Guid> pinned) =>
        members
            .OrderBy(m => pinned.Contains(m.Id) ? 0 : 1)
            .ThenBy(m => StatusRank(m.HealthStatus))
            .ThenBy(m => m.FirstName, StringComparer.CurrentCultureIgnoreCase)
            // Last, so two members with the same first name and status never swap places between
            // one refresh and the next.
            .ThenBy(m => m.Id)
            .Select(m => m.Id)
            .ToList();

    /// <summary>Pinned ids as stored on the phone: comma-separated, anything unreadable dropped.</summary>
    public static IReadOnlySet<Guid> ParsePins(string? stored) =>
        (stored ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => Guid.TryParse(s, out var id) ? id : (Guid?)null)
            .OfType<Guid>()
            .ToHashSet();

    public static string FormatPins(IEnumerable<Guid> pinned) =>
        string.Join(',', pinned.Select(id => id.ToString("N")).Order(StringComparer.Ordinal));
}
