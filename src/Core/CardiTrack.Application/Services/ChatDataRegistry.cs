using CardiTrack.Application.DTOs.Common;

namespace CardiTrack.Application.Services;

/// <summary>
/// One data source as a planning prompt describes it: what the app collects there and what it
/// stands for. The registry half of the library the member-chat design names — data points and
/// their meaning — with the formulas and inference limits carried by
/// <see cref="ChatWorkflowCatalogue"/>'s claim classes rather than here.
/// </summary>
/// <param name="Kind">The whitelisted source this entry describes.</param>
/// <param name="Line">
/// The line rendered into a planning prompt. Starts with the enum member's exact name because
/// that name is what the model must answer with and what the parser matches.
/// </param>
public sealed record ChatDataRegistryEntry(DataQueryKind Kind, string Line);

/// <summary>
/// A published reference range for one daily metric, with its attribution — the second benchmark
/// the analysis and inference rungs compare against, beside the member's own baseline.
/// </summary>
/// <param name="Metric">The charted metric the band describes.</param>
/// <param name="Line">The band as a prompt states it, attribution included.</param>
/// <param name="Authority">
/// The publishing body's full name — the closed vocabulary the inference read names its
/// references from. The model picks <em>which</em> authority its verdict drew on; it never
/// composes citation text.
/// </param>
/// <param name="Citation">
/// The authority line quoted verbatim at the end of an inference reply. Fixed text here rather
/// than model output for the reason every guard on this platform exists: a small model asked to
/// cite writes citations that sound right, and the one thing a quoted authority must be is real.
/// </param>
/// <param name="Url">
/// Where the band is actually published, when the authority has a canonical page for it — the
/// client renders the authority in a quoted Reference line as a link to this. Null when there is
/// no page to stand behind the figure (the WHO breathing band is textbook consensus rather than a
/// single publication), and the citation then renders as plain text: no link beats a link that
/// substantiates nothing.
/// </param>
public sealed record PublishedBand(
    ChartMetricKind Metric, string Line, string Authority, string Citation, string? Url = null);

/// <summary>
/// The dataset registry: every data source a member-chat planning call may be offered, and the
/// published reference bands the data rungs benchmark against. Constants in the Application
/// layer, reviewed like an alert rule — the same standing as <see cref="ChatWorkflowCatalogue"/>,
/// because together they are the whole vocabulary the chat's model calls decide in.
/// </summary>
/// <remarks>
/// <para>
/// <b>Rendered per workflow, never into routing.</b> The routing call classifies and must not
/// carry data vocabulary (see <c>docs/technical/member_chat_routing.md</c> §3). Each data
/// workflow's own planning call renders <see cref="For"/> filtered to that workflow's
/// <see cref="ChatWorkflowDefinition.AllowedDatasets"/>, so the planner is only ever offered what
/// the validator will accept — one object serving both, the same way the catalogue serves the
/// router and its validator.
/// </para>
/// <para>
/// <b>The bands are deliberately incomplete.</b> Resting heart rate, sleep and breathing rate
/// have published, attributable typical ranges; steps and overnight HRV do not — no accredited
/// body publishes a universal daily-step band, and HRV varies too much person to person for a
/// general band to be honest — so those are benchmarked against the member's own baseline alone,
/// and the prompt says so rather than letting a model invent "10,000 steps" as if it were
/// guidance.
/// </para>
/// </remarks>
public static class ChatDataRegistry
{
    /// <summary>Every source the whitelist can actually fetch — the registry is a description of
    /// capability, so an entry with no fetch path would be a lie the planner acts on.</summary>
    public static IReadOnlyList<ChatDataRegistryEntry> All { get; } =
        Array.AsReadOnly(new ChatDataRegistryEntry[]
        {
            new(DataQueryKind.RecentActivity,
                "RecentActivity — daily steps, resting heart rate, sleep, overnight heart rate "
                + "variability and overnight breathing rate over the last several days; each "
                + "figure is a finished day's total or nightly figure, never a live reading"),
            new(DataQueryKind.Baseline,
                "Baseline — the member's own established pattern (typical steps, resting heart "
                + "rate, sleep), the reference for whether a reading is usual for them"),
            new(DataQueryKind.UnresolvedAlerts,
                "UnresolvedAlerts — alerts raised for this member that nobody has acknowledged "
                + "yet"),
            new(DataQueryKind.RealtimeAssessments,
                "RealtimeAssessments — recent hour-by-hour heart-rate severity assessments"),
        });

    /// <summary>
    /// The registry slice one workflow's planning call may be offered — <see cref="All"/>
    /// intersected with the catalogue entry's allowed datasets, in registry order.
    /// </summary>
    public static IReadOnlyList<ChatDataRegistryEntry> For(IReadOnlyList<DataQueryKind> allowed) =>
        All.Where(e => allowed.Contains(e.Kind)).ToList();

    /// <summary>
    /// Published typical ranges, attributed — see the class remarks for why steps has none.
    /// Rendered into the analysis and inference clinical prompts, never into routing or steer.
    /// </summary>
    public static IReadOnlyList<PublishedBand> Bands { get; } =
        Array.AsReadOnly(new PublishedBand[]
        {
            new(ChartMetricKind.RestingHeartRate,
                "Resting heart rate: 60–100 bpm is the typical adult range (American Heart "
                + "Association); athletes and some medications sit legitimately below it",
                Authority: "American Heart Association",
                Citation: "American Heart Association — typical adult resting heart rate 60–100 bpm",
                Url: "https://www.heart.org/en/health-topics/high-blood-pressure/the-facts-about-high-blood-pressure/all-about-heart-rate-pulse"),
            new(ChartMetricKind.Sleep,
                "Sleep: 7–9 hours a night is the recommendation for adults, 7–8 hours for adults "
                + "65 and over (National Sleep Foundation)",
                Authority: "National Sleep Foundation",
                Citation: "National Sleep Foundation — recommended nightly sleep 7–9 hours for "
                + "adults, 7–8 hours from 65",
                Url: "https://doi.org/10.1016/j.sleh.2014.12.010"),
            new(ChartMetricKind.OvernightBreathingRate,
                "Breathing rate: 12–20 breaths per minute is the typical adult resting range "
                + "(WHO); overnight averages sit toward its lower half",
                Authority: "World Health Organization",
                Citation: "World Health Organization — typical adult resting breathing rate "
                + "12–20 breaths per minute"),
        });

    /// <summary>
    /// The citation lines for the authorities an inference read says its verdict drew on — the
    /// registry's own fixed text, in registry order, deduplicated, with anything not in the
    /// closed set dropped rather than thrown. The model chooses <em>which</em>; this method is the
    /// only author of <em>what</em>, which is what makes a quoted authority worth reading: it is
    /// checkably the range the prompt actually carried, never a study the model remembered.
    /// </summary>
    public static IReadOnlyList<string> CitationsFor(IEnumerable<string> authorities)
    {
        var named = authorities
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // The band lines attribute "(WHO)" while the authority reads "World Health Organization",
        // and the model may echo either spelling — both name the same body, so both match.
        static string Initials(string authority) =>
            new(authority.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => w[0]).ToArray());

        return Bands
            .Where(b => named.Contains(b.Authority) || named.Contains(Initials(b.Authority)))
            .Select(b => b.Citation)
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// The bands as one prompt block, with the two rules that keep a band from overreaching: a
    /// figure outside a published range need not be abnormal for this person, and a metric with no
    /// published range is compared against the member's own baseline only.
    /// </summary>
    public static string BandsBlock { get; } =
        "--- Published typical ranges ---\n"
        + string.Join("\n", Bands.Select(b => $"  {b.Line}"))
        + "\n  Steps and overnight heart rate variability have no published typical range — "
        + "compare them against this member's own baseline only, and say so if asked whether "
        + "such a figure is \"good\"; HRV in particular varies too much person to person for any "
        + "general band to be honest."
        + "\n  A reading outside a published range is not by itself abnormal for this person; "
        + "their own baseline says what is usual for them. Attribute any published range you cite.";
}
