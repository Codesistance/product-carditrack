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
public sealed record PublishedBand(ChartMetricKind Metric, string Line);

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
/// <b>The bands are deliberately incomplete.</b> Resting heart rate and sleep have published,
/// attributable typical ranges; steps do not — no accredited body publishes a universal daily-step
/// band — so a steps question is benchmarked against the member's own baseline alone, and the
/// prompt says so rather than letting a model invent "10,000 steps" as if it were guidance.
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
                "RecentActivity — daily steps, resting heart rate and sleep over the last several "
                + "days; each figure is a finished day's total or nightly figure, never a live "
                + "reading"),
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
                + "Association); athletes and some medications sit legitimately below it"),
            new(ChartMetricKind.Sleep,
                "Sleep: 7–9 hours a night is the recommendation for adults, 7–8 hours for adults "
                + "65 and over (National Sleep Foundation)"),
        });

    /// <summary>
    /// The bands as one prompt block, with the two rules that keep a band from overreaching: a
    /// figure outside a published range need not be abnormal for this person, and a metric with no
    /// published range is compared against the member's own baseline only.
    /// </summary>
    public static string BandsBlock { get; } =
        "--- Published typical ranges ---\n"
        + string.Join("\n", Bands.Select(b => $"  {b.Line}"))
        + "\n  Steps has no published typical range — compare steps against this member's own "
        + "baseline only, and say so if asked whether a step count is \"good\"."
        + "\n  A reading outside a published range is not by itself abnormal for this person; "
        + "their own baseline says what is usual for them. Attribute any published range you cite.";
}
