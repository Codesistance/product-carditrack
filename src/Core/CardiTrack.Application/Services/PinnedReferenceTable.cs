using System.Globalization;

namespace CardiTrack.Application.Services;

/// <summary>
/// The curated, versioned table of clinical norms injected into the trend prompt
/// (<c>docs/llm_design.md</c> — "Pinned reference ranges"), so the model never recalls a benchmark
/// from its training data and the yardstick behind every narrative is reviewable.
/// </summary>
/// <remarks>
/// <para>
/// Assembled from what this codebase already cites rather than written fresh:
/// <see cref="HealthReferenceRanges"/> holds the bands the dashboard and the journal books already
/// shade charts with, and <see cref="WellnessGuidelines"/> holds the publishing body and URL behind
/// each. A second table would be a second set of numbers to keep in agreement with the first, and
/// the failure mode is a model told one range while a chart draws another.
/// </para>
/// <para>
/// <b>What is deliberately absent:</b> resting heart rate by age and sex. The design sketch names
/// it, and this codebase has no sourced table for it — the AHA citation it does have is a single
/// adult range. Writing one here from memory is exactly the thing pinning the table exists to
/// prevent, so the table carries the range that is sourced and says nothing about age. Adding the
/// finer breakdown is a sourcing job, not a code one.
/// </para>
/// <para>
/// <see cref="Version"/> is stamped on every row written from it. A change to the numbers here is
/// therefore a change every stored narrative can be dated against — which is what makes "the model
/// was told X at the time" answerable months later.
/// </para>
/// </remarks>
public static class PinnedReferenceTable
{
    /// <summary>
    /// Bumped whenever a figure, a band or a citation below changes. Not bumped for wording.
    /// </summary>
    public const int Version = 2;

    /// <summary>
    /// The block as the prompt carries it. Ages are resolved against the member so the sleep band
    /// is the one that applies to them rather than a table the model has to pick a row from.
    /// </summary>
    public static string For(int ageYears)
    {
        var sleep = HealthReferenceRanges.Sleep(ageYears);
        var heart = HealthReferenceRanges.RestingHeartRate;
        var oxygen = HealthReferenceRanges.SpO2;

        var lines = new List<string>
        {
            $"Pinned reference ranges (version {Version}). These are the only published figures you "
            + "may treat as authoritative. Do not recall others.",
            $"- Resting heart rate: {heart.Low:0}-{heart.High:0} bpm typical for an adult at rest "
            + $"({heart.Source}).",
            // Stated both ways, for the reason the activity line below is. The published figure is
            // in hours; the sleep trend feature reports minutes a night, and the brief forbids the
            // model converting anything for itself — so hours alone left it a band it was not
            // allowed to compare its own figure to. The minutes are named as the same figure so
            // the two cannot read as two recommendations.
            $"- Sleep: {sleep.Low:0.#}-{sleep.High:0.#} hours a night recommended at this member's "
            + $"age ({sleep.Source}) — which is {sleep.Low * MinutesPerHour:0}-"
            + $"{sleep.High * MinutesPerHour:0} minutes a night, the same recommendation in the "
            + "units the sleep figures are reported in, not a second one.",
            $"- Blood oxygen: {oxygen.Low:0}-{oxygen.High:0}% normal at sea level ({oxygen.Source}).",
            // No published band for breathing asleep, which is the only breathing figure the trend
            // features carry. HealthReferenceRanges.BreathingRate is WHO's rate at rest and is a
            // different measurement — quoting it here would hand the model a yardstick for a
            // metric it does not measure.
            $"- Breathing asleep: {HealthReferenceRanges.NoOvernightBreathingBand}",
            // Stated both ways on purpose. The published figure is weekly; the trend features
            // report active minutes per day, and the brief forbids the model from converting
            // anything for itself — so a weekly band beside a daily figure would be two numbers it
            // is not allowed to compare. The daily equivalent is given here, and named as derived.
            "- Physical activity: at least 150-300 minutes a week of moderate aerobic activity for "
            + "an adult (World Health Organization) — about 21-43 minutes a day, which is that "
            + "same published figure divided across the week, not a separate recommendation.",
            $"- Heart rate variability: {HealthReferenceRanges.NoHeartRateVariabilityBand}",
        };

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// The publishing bodies behind the block, for a reader who wants the sources rather than the
    /// numbers. Kept as the same list the Advise path cites from, so the two cannot disagree about
    /// who said what.
    /// </summary>
    public static string Citations() =>
        string.Join(
            Environment.NewLine,
            WellnessGuidelines.All.Select(reference =>
                $"- {reference.Citation} ({reference.Url})"));

    /// <summary>
    /// Minutes in an hour, named because the sleep line converts with it. The conversion is done
    /// here, once, in .NET — which is the whole point of the line: the model is told never to
    /// convert a unit, so anything it needs in two units has to arrive in two units.
    /// </summary>
    private const decimal MinutesPerHour = 60m;

    /// <summary>The version as prompt-safe text, for the stamp a stored narrative carries.</summary>
    public static string VersionLabel =>
        Version.ToString(CultureInfo.InvariantCulture);
}
