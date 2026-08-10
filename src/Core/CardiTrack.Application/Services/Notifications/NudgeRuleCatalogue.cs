using CardiTrack.Application.Services.Notifications.Rules;

namespace CardiTrack.Application.Services.Notifications;

/// <summary>
/// The Phase 1 rule set, in code rather than in the database.
/// </summary>
/// <remarks>
/// Database-driven rules would allow copy changes without a deploy, at the cost of putting untested
/// predicates into production data. With the catalogue this small, the deploy is the cheaper price —
/// revisit if it passes roughly ten rules.
/// </remarks>
public static class NudgeRuleCatalogue
{
    public static IReadOnlyList<INudgeRule> All { get; } =
    [
        // Safety first — these are the ones that mean monitoring is not working.
        new DeviceAuthBrokenRule(),

        // Blocking — core value is unavailable until they close.
        new DeviceRemovedRule(),
        new DeviceStaleLongRule(),
        new TimezoneDefaultRule(),
        new BaselineStalledRule(),

        // Unlock — supply this, get that.
        new SleepScopeMissingRule(),
        new MedicalNotesEmptyRule(),

        // Account lifecycle.
        new PauseLeftLongRule()
    ];

    public static INudgeRule? Find(string ruleCode) =>
        All.FirstOrDefault(r => r.RuleCode == ruleCode);

    /// <summary>Localization key for a rule's copy, optionally in a variant.</summary>
    public static string TitleKey(string ruleCode, string? variant = null) => Key(ruleCode, "title", variant);
    public static string BodyKey(string ruleCode, string? variant = null) => Key(ruleCode, "body", variant);
    public static string BenefitKey(string ruleCode, string? variant = null) => Key(ruleCode, "benefit", variant);

    private static string Key(string ruleCode, string part, string? variant) =>
        variant is null
            ? $"nudge.{ruleCode}.{part}"
            : $"nudge.{ruleCode}.{variant}.{part}";
}
