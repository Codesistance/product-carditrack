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

    public static NudgeVerdict Gap(
        string deepLink,
        string discriminator = "",
        IReadOnlyDictionary<string, object>? templateData = null,
        string? variant = null) => new()
        {
            HasGap = true,
            ActionDeepLink = deepLink,
            Discriminator = discriminator,
            TemplateData = templateData ?? new Dictionary<string, object>(),
            Variant = variant
        };
}
