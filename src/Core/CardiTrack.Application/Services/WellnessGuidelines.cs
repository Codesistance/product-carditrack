using System.Text.RegularExpressions;

namespace CardiTrack.Application.Services;

/// <summary>
/// One citable wellness authority behind an Advise suggestion.
/// </summary>
/// <param name="Authority">The publishing body, as a caregiver would recognise it.</param>
/// <param name="Citation">
/// The authority line quoted at the end of a served advise reply — fixed text, for the same
/// reason <see cref="PublishedBand.Citation"/> is: the model only ever picks which reference
/// grounded a suggestion, and the words a caregiver reads as an authority come from here.
/// </param>
/// <param name="Match">
/// Whole-word markers that recognise this authority in a stored
/// <see cref="CardiTrack.Domain.Entities.MemberAdvise.GuidelineCited"/> — the model's pick is
/// free text in a few words ("Adult physical activity (WHO, 2020)"), and rows written before
/// this class existed must map too.
/// </param>
/// <param name="Url">
/// Where the cited guidance is actually published, so a caregiver who wants to read the source
/// can be taken to it — the client renders the authority in a served Reference line as a link to
/// this. Fixed here beside the citation text it substantiates, and for the same reason: a URL a
/// model composed would sound right and lead nowhere.
/// </param>
public sealed record WellnessReference(string Authority, string Citation, Regex Match, string Url);

/// <summary>
/// The closed set of wellness authorities Advise suggestions are grounded in — the structured
/// half of the references <c>MedicalPromptBlocks.WellnessGuidelineReference</c> renders into the
/// generation prompt (a drift test holds the two to the same figures). The same traceability
/// pattern as inference's <see cref="ChatDataRegistry.CitationsFor"/>: the model picks WHICH
/// reference, this class is the only author of WHAT gets quoted, and a pick that names nothing
/// here quotes nothing rather than something invented.
/// </summary>
public static partial class WellnessGuidelines
{
    public static IReadOnlyList<WellnessReference> All { get; } =
        Array.AsReadOnly(new WellnessReference[]
        {
            new("World Health Organization",
                "World Health Organization (2020) — adult physical activity: at least 150-300 "
                + "minutes a week of moderate aerobic activity, or 75-150 minutes vigorous",
                ActivityMarkers(),
                "https://www.who.int/publications/i/item/9789240015128"),
            new("National Sleep Foundation",
                "National Sleep Foundation — recommended nightly sleep 7–9 hours for "
                + "adults, 7–8 hours from 65",
                SleepMarkers(),
                "https://doi.org/10.1016/j.sleh.2014.12.010"),
            new("American Heart Association",
                "American Heart Association — typical adult resting heart rate 60-100 bpm at "
                + "rest, lower in well-conditioned adults",
                HeartMarkers(),
                "https://www.heart.org/en/health-topics/high-blood-pressure/the-facts-about-high-blood-pressure/all-about-heart-rate-pulse"),
        });

    /// <summary>
    /// The citation for a stored <c>GuidelineCited</c>, or null when it names no authority in the
    /// set — the honest outcome for the wearable-caveat bullet and for anything a model composed
    /// that the closed set does not carry. Null means the reply quotes nothing, never a guess.
    /// </summary>
    public static string? CitationFor(string? guidelineCited)
    {
        if (string.IsNullOrWhiteSpace(guidelineCited))
            return null;

        return All.FirstOrDefault(r => r.Match.IsMatch(guidelineCited))?.Citation;
    }

    /// <remarks>
    /// The bare word "activity" used to match here, and it is why a caregiver was shown the WHO's
    /// 150-300 minutes a week under a suggestion whose evidence was a step count. The generation
    /// prompt invites a finding measured against "the reference below <em>or of their own usual</em>",
    /// the reference block carries no step band — no accredited body publishes one — and
    /// <c>guidelineCited</c> is required, so an activity shortfall found against the member's own
    /// baseline had to name something. "Activity baseline" matched, and a minutes-per-week citation
    /// was attached to a figure measured in steps, which a caregiver cannot check against it.
    /// <para>
    /// So the marker now takes the authority or the full phrase, as the sleep and heart markers
    /// effectively do. A row naming the member's own baseline matches nothing here and quotes
    /// nothing, which is the honest outcome and the same one <c>ChatDataRegistry.CitationsFor</c>
    /// reaches for an inference verdict resting on the baseline alone.
    /// </para>
    /// </remarks>
    [GeneratedRegex(@"\b(?:WHO|World Health Organi[sz]ation|physical activity)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ActivityMarkers();

    /// <remarks>
    /// Authority names only, matching the activity marker. The bare word "sleep" used to match
    /// here and attached the sleep citation to "usual sleep" / "sleep baseline" — the same mismatch
    /// the WHO activity marker was tightened to stop. Old AASM/CDC stored picks quote nothing
    /// until Advise regenerates onto this prompt version.
    /// </remarks>
    [GeneratedRegex(@"\b(?:NSF|National Sleep Foundation)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SleepMarkers();

    /// <remarks>
    /// Authority names only. The bare word "heart" attached the AHA resting-rate citation to
    /// "heart rate baseline" the same way "sleep" did.
    /// </remarks>
    [GeneratedRegex(@"\b(?:AHA|American Heart Association)\b", RegexOptions.IgnoreCase)]
    private static partial Regex HeartMarkers();
}
