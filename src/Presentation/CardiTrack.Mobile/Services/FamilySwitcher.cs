using CardiTrack.Mobile.Core.Family;

namespace CardiTrack.Mobile.Services;

/// <summary>
/// One family as the switcher drawer draws it: what it is called, what the caregiver is in it,
/// and whether anything in it is asking for attention.
/// </summary>
public sealed record FamilySwitcherRow(
    Guid OrganizationId,
    string Name,
    string RoleLabel,
    FamilyAlertSummary Alerts,
    bool IsCurrent);

/// <summary>What the caregiver did with the drawer.</summary>
public abstract record FamilySwitcherChoice
{
    /// <summary>Show this family in the tab.</summary>
    public sealed record Chosen(Guid OrganizationId) : FamilySwitcherChoice;

    /// <summary>The drawer's foot: start a family of their own.</summary>
    public sealed record StartFamily : FamilySwitcherChoice;

    /// <summary>The drawer's foot: join one with a Family ID.</summary>
    public sealed record JoinFamily : FamilySwitcherChoice;

    public static readonly FamilySwitcherChoice StartFamilyChoice = new StartFamily();

    public static readonly FamilySwitcherChoice JoinFamilyChoice = new JoinFamily();
}
