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
            new("AASM/CDC consensus",
                "AASM/CDC consensus — adult sleep duration: 7 or more hours a night",
                SleepMarkers(),
                "https://doi.org/10.5664/jcsm.4758"),
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

    [GeneratedRegex(@"\b(?:WHO|physical activity|activity)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ActivityMarkers();

    [GeneratedRegex(@"\b(?:AASM|CDC|sleep)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SleepMarkers();

    [GeneratedRegex(@"\b(?:AHA|heart)\b", RegexOptions.IgnoreCase)]
    private static partial Regex HeartMarkers();
}
