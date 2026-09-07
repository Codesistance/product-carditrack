using System.Text.RegularExpressions;
using CardiTrack.Application.DTOs.Common;
using CardiTrack.Domain.Entities;

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
public static partial class ChatDataRegistry
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
            // "Alerts nobody has acknowledged yet" described the row and not the question it
            // answers, so a planner told to "pick only what the question actually needs" read past
            // it whenever the question named no metric — which is exactly when it is needed. "Is
            // there anything outstanding" is what a caregiver asking "anything to follow up on?"
            // means, and this is the only source that knows.
            new(DataQueryKind.UnresolvedAlerts,
                "UnresolvedAlerts — alerts raised for this member that nobody has acknowledged "
                + "yet: what is still outstanding, and the source for any question about whether "
                + "anything needs attention or has been missed"),
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
    /// <remarks>
    /// This overload answers only "is the name real". Whether the verdict actually used the band
    /// is the other overload's question, and the one an inference reply must ask — see there for
    /// the footer this one produced on its own.
    /// </remarks>
    public static IReadOnlyList<string> CitationsFor(IEnumerable<string> authorities) =>
        NamedBands(authorities).Select(b => b.Citation).Distinct().ToList();

    /// <summary>
    /// The citation lines a verdict has earned: named by the model, <em>and</em> about a metric
    /// the readings block actually carried, <em>and</em> about a metric the verdict text actually
    /// mentions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Matching on the authority name alone put the same three-line References footer under
    /// every inference reply (observed 2026-09-07). The model is shown all three bands on every
    /// call, in <see cref="BandsBlock"/>, and asked which it drew on; a small model answers by
    /// echoing the list. Nothing then checked the pick against what the verdict said or what
    /// data it had, so a verdict about heart rate quoted the sleep and breathing authorities too
    /// — and a caregiver reading three citations under a one-sentence answer learns to skip the
    /// footer, which is the opposite of what a citation is for.
    /// </para>
    /// <para>
    /// Two checks, both in code, both about what the model was actually given rather than what
    /// it claims. The metric must be present in the fetched readings: a band the verdict could
    /// not have compared anything against was not drawn on, whatever the model says. And the
    /// metric's words must appear in the verdict: an authority for a reading the verdict never
    /// mentions is a citation to nothing the caregiver read. The model still picks
    /// <em>which</em>; this narrows the pick to what the reply can stand behind, the same
    /// posture as <c>MemberChatReplies.ResolveSpan</c> dropping a date outside the window that
    /// was fetched.
    /// </para>
    /// </remarks>
    /// <param name="authorities">What the model named — the closed-vocabulary pick.</param>
    /// <param name="analysis">The clinical read's own text, the verdict the citations sit under.</param>
    /// <param name="fetched">What the whitelist actually put in front of the model.</param>
    public static IReadOnlyList<string> CitationsFor(
        IEnumerable<string> authorities, string analysis, FetchedMemberData fetched) =>
        NamedBands(authorities)
            .Where(b => WasFetched(b.Metric, fetched.RecentActivity) && MetricWords(b.Metric).IsMatch(analysis))
            .Select(b => b.Citation)
            .Distinct()
            .ToList();

    /// <summary>The bands whose authority the model named, in registry order.</summary>
    private static IEnumerable<PublishedBand> NamedBands(IEnumerable<string> authorities)
    {
        var named = authorities
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // The band lines attribute "(WHO)" while the authority reads "World Health Organization",
        // and the model may echo either spelling — both name the same body, so both match.
        static string Initials(string authority) =>
            new(authority.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => w[0]).ToArray());

        return Bands.Where(b => named.Contains(b.Authority) || named.Contains(Initials(b.Authority)));
    }

    /// <summary>Whether any fetched day carried a value for the band's metric — the band was in
    /// front of the model either way; the reading it compares against was not.</summary>
    private static bool WasFetched(ChartMetricKind metric, IReadOnlyList<ActivityLog> recent) =>
        recent.Any(l => metric switch
        {
            ChartMetricKind.RestingHeartRate => l.RestingHeartRate is not null,
            ChartMetricKind.Sleep => l.SleepMinutes is not null,
            ChartMetricKind.OvernightBreathingRate => l.OvernightBreathingRate is not null,
            _ => false,
        });

    /// <summary>
    /// Whether generated prose — a clinical verdict, or the stored status-line caption — names
    /// this reading. Whole words in the model's own register ("resting HR", "bpm", "steps"), not
    /// caregiver vernacular: that judgement is the router's. The citation filter and the status
    /// caption-append share this so they cannot disagree about what counts as naming the heart.
    /// </summary>
    public static bool Mentions(StatusMetric metric, string prose) =>
        !string.IsNullOrWhiteSpace(prose) && WordsFor(metric).IsMatch(prose);

    /// <summary>
    /// The first reading the prose names, walking <see cref="StatusMetric"/> in declaration
    /// order so "heart rate variability" is not a heart-rate caption. Null when it names none.
    /// Used to decide whether a stored caption is about the same reading a status reply just
    /// stated — classifying our own generated line, not the caregiver's question.
    /// </summary>
    public static StatusMetric? PrimaryMetricNamed(string prose)
    {
        if (string.IsNullOrWhiteSpace(prose))
            return null;

        foreach (var metric in Enum.GetValues<StatusMetric>())
        {
            if (Mentions(metric, prose))
                return metric;
        }

        return null;
    }

    /// <summary>
    /// The words a generated line uses when it is about a reading. Deliberately loose within
    /// whole words: the clinical read writes "resting HR", "heart rate" and "bpm" for the same
    /// thing, and a verdict that names the reading in any of its spellings has named it.
    /// </summary>
    private static Regex MetricWords(ChartMetricKind metric) => metric switch
    {
        ChartMetricKind.RestingHeartRate => WordsFor(StatusMetric.RestingHeartRate),
        ChartMetricKind.Sleep => WordsFor(StatusMetric.Sleep),
        ChartMetricKind.OvernightBreathingRate => WordsFor(StatusMetric.BreathingRate),
        _ => NothingMatches(),
    };

    private static Regex WordsFor(StatusMetric metric) => metric switch
    {
        StatusMetric.HeartRateVariability => HrvWords(),
        StatusMetric.RestingHeartRate => HeartRateWords(),
        StatusMetric.Oxygen => OxygenWords(),
        StatusMetric.BreathingRate => BreathingWords(),
        StatusMetric.Sleep => SleepWords(),
        StatusMetric.Steps => StepsWords(),
        _ => NothingMatches(),
    };

    [GeneratedRegex(@"\b(?:hrv|heart rate variability|variability)\b", RegexOptions.IgnoreCase)]
    private static partial Regex HrvWords();

    [GeneratedRegex(@"\b(?:heart rate(?! variability)|heart(?! rate)|pulse|bpm|resting hr|hr)\b", RegexOptions.IgnoreCase)]
    private static partial Regex HeartRateWords();

    [GeneratedRegex(@"\b(?:oxygen|spo2|o2|saturation)\b", RegexOptions.IgnoreCase)]
    private static partial Regex OxygenWords();

    [GeneratedRegex(@"\b(?:breath\w*|respirat\w*)\b", RegexOptions.IgnoreCase)]
    private static partial Regex BreathingWords();

    [GeneratedRegex(@"\b(?:sleep\w*|slept|asleep|nights?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SleepWords();

    [GeneratedRegex(@"\b(?:steps?|walk\w*|activity|active|moving|movement)\b", RegexOptions.IgnoreCase)]
    private static partial Regex StepsWords();

    /// <summary>A band for a metric this map does not know quotes nothing — the same direction
    /// every other drop here takes.</summary>
    [GeneratedRegex(@"(?!)")]
    private static partial Regex NothingMatches();

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
