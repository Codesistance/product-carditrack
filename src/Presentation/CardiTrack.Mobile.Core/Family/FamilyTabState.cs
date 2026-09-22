using CardiTrack.Application.DTOs.Responses;

namespace CardiTrack.Mobile.Core.Family;

/// <summary>The three shapes the Family tab takes (Story 4.9).</summary>
public enum FamilyTabMode
{
    /// <summary>No family at all — a guest, with or without an ask outstanding.</summary>
    NoFamily,

    /// <summary>In the selected family as a member: the admin, what they can see, everyone in it, and Leave.</summary>
    Member,

    /// <summary>Runs the selected family: its Family ID, the queue, the roster, and who pays.</summary>
    Admin,
}

public sealed record FamilyTabSelection(FamilyTabMode Mode, FamilySummary? Family);

public static class FamilyTabState
{
    public const string AdminRole = "admin";

    /// <summary>
    /// Which family the tab shows, and in what role. The drawer's last choice wins if it is still
    /// one of the caller's families; otherwise the home family, and failing that the first.
    /// </summary>
    public static FamilyTabSelection Resolve(IReadOnlyList<FamilySummary> families, Guid? preferredOrganizationId)
    {
        ArgumentNullException.ThrowIfNull(families);
        if (families.Count == 0)
            return new FamilyTabSelection(FamilyTabMode.NoFamily, null);

        var family = (preferredOrganizationId is { } preferred
                ? families.FirstOrDefault(f => f.OrganizationId == preferred)
                : null)
            ?? families.FirstOrDefault(f => f.IsHomeFamily)
            ?? families[0];

        return new FamilyTabSelection(IsAdmin(family.Role) ? FamilyTabMode.Admin : FamilyTabMode.Member, family);
    }

    public static bool IsAdmin(string? role) =>
        string.Equals(role?.Trim(), AdminRole, StringComparison.OrdinalIgnoreCase);
}
