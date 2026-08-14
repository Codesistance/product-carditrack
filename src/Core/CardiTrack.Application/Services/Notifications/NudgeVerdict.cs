using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Services.Notifications;

/// <summary>
/// A rule's answer for one context: either the gap is absent, or it is present with the detail
/// needed to render and identify it.
/// </summary>
public sealed record NudgeVerdict
{
    /// <summary>The gap is not present. The reconciler resolves any stored row for this rule.</summary>
    public static readonly NudgeVerdict NoGap = new() { HasGap = false };

    public bool HasGap { get; private init; }

    /// <summary>
    /// Distinguishes one instance of a gap from another within the same rule and scope — a
    /// connection id, a fortnight bucket. It is what decides whether a *changed* gap re-arms after
    /// a dismissal or is treated as the same one continuing.
    /// </summary>
    public string Discriminator { get; private init; } = string.Empty;

    /// <summary>Substitutions for the copy. Counters only — never a name or a metric value.</summary>
    public IReadOnlyDictionary<string, object> TemplateData { get; private init; }
        = new Dictionary<string, object>();

    /// <summary>Deep link to the exact field that closes this gap.</summary>
    public string ActionDeepLink { get; private init; } = string.Empty;

    /// <summary>
    /// Optional copy variant, appended to the rule's localization keys — lets one rule speak
    /// differently about materially different causes without becoming two rules.
    /// </summary>
    public string? Variant { get; private init; }

    /// <summary>
    /// Overrides <see cref="NudgeSpec.Priority"/> for this instance of the gap. Null for every
    /// rule whose severity does not vary by instance — the overwhelming majority, where the
    /// rule-level <see cref="NudgeSpec.Priority"/> already says everything there is to say. Exists
    /// for the rare rule (device battery, so far) that reports the same gap at genuinely different
    /// severities depending on what it found — priority only, never delivery: category still
    /// decides push/critical-flag/quiet-hours behaviour, so this cannot make one instance of a
    /// Safety gap ring louder than another.
    /// </summary>
    public NotificationPriority? Priority { get; private init; }

    public static NudgeVerdict Gap(
        string deepLink,
        string discriminator = "",
        IReadOnlyDictionary<string, object>? templateData = null,
        string? variant = null,
        NotificationPriority? priority = null) => new()
        {
            HasGap = true,
            ActionDeepLink = deepLink,
            Discriminator = discriminator,
            TemplateData = templateData ?? new Dictionary<string, object>(),
            Variant = variant,
            Priority = priority
        };
}
