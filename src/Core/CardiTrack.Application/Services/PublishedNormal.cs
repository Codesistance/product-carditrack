using System.Globalization;

namespace CardiTrack.Application.Services;

/// <summary>
/// What "normal" means for a reading, stated once for every model that judges one: for sleep,
/// resting heart rate and blood oxygen it is the published range, and the member's own usual is
/// context.
/// </summary>
/// <remarks>
/// <para>
/// Decision 2026-09-25. Until then the prompts held the opposite: chat's bands block said "a
/// reading outside a published range is not by itself abnormal for this person; their own baseline
/// says what is usual for them", the alert judgement judged "against the person's own usual first",
/// and most narratives were shown no published range at all — so "normal" meant whatever the
/// member had been doing for thirty days. A person sleeping five hours a night for a month had a
/// usual of five hours, and a five-hour night read as a settled one. The usual can only say whether
/// a reading is new for someone; it cannot say whether it is healthy, and a person living poorly
/// is exactly the person whose usual hides it.
/// </para>
/// <para>
/// Three metrics, because only three have a published adult range this codebase can cite for the
/// figure it actually measures (<see cref="HealthReferenceRanges"/>). Breathing while asleep, heart
/// rate variability and steps have none — WHO's 12–20 is a waking rate at rest, and printing it
/// beside an overnight figure grades it against a measurement it is not
/// (<see cref="HealthReferenceRanges.NoOvernightBreathingBand"/>) — so those stay judged against the
/// member's own usual, and the rule says so rather than leaving the model to guess.
/// </para>
/// <para>
/// A rule and the member's own ranges, never one without the other: a rule naming ranges the
/// prompt does not carry is a rule the model fills in from memory, which is the recall
/// <see cref="PinnedReferenceTable"/> exists to prevent.
/// </para>
/// </remarks>
public static class PublishedNormal
{
    /// <summary>
    /// The rule, for a clinical brief. Phrased as facts about the readings rather than as example
    /// sentences — a small model echoes an example back as the answer.
    /// </summary>
    public const string Rule =
        "For sleep, resting heart rate and blood oxygen, the published range listed with the "
        + "readings is what normal means. A reading outside it is worth attention even when it is "
        + "usual for this person: a usual that sits outside the range is outside the range too, and "
        + "does not make the reading normal. This person's own usual is context — it says whether a "
        + "reading is new for them, never whether it is healthy. Breathing while asleep, heart rate "
        + "variability and steps have no published range: judge those against this person's own "
        + "usual only.";

    /// <summary>
    /// The member's own published ranges, one line each — sleep resolved to their age, both
    /// age bands when the age is unknown. Hours and minutes both, for the reason
    /// <see cref="PinnedReferenceTable"/> gives: the sleep figures arrive in either unit depending
    /// on the prompt, and a model is never asked to convert one.
    /// </summary>
    public static string Ranges(int? ageYears)
    {
        var heart = HealthReferenceRanges.RestingHeartRate;
        var oxygen = HealthReferenceRanges.SpO2;

        string sleepLine;
        if (ageYears is { } age)
        {
            var sleep = HealthReferenceRanges.Sleep(age);
            sleepLine = string.Create(CultureInfo.InvariantCulture,
                $"- Sleep: {sleep.Low:0.#}-{sleep.High:0.#} hours a night ({sleep.Low * 60:0}-{sleep.High * 60:0} minutes), recommended at this member's age ({sleep.Source}).");
        }
        else
        {
            var adult = HealthReferenceRanges.Sleep(HealthReferenceRanges.OlderAdultAge - 1);
            var older = HealthReferenceRanges.Sleep(HealthReferenceRanges.OlderAdultAge);
            sleepLine = string.Create(CultureInfo.InvariantCulture,
                $"- Sleep: {adult.Low:0.#}-{adult.High:0.#} hours a night for adults, {older.Low:0.#}-{older.High:0.#} from {HealthReferenceRanges.OlderAdultAge} ({adult.Source}).");
        }

        return string.Join(
            "\n",
            "--- Published normal ranges ---",
            sleepLine,
            string.Create(CultureInfo.InvariantCulture,
                $"- Resting heart rate: {heart.Low:0}-{heart.High:0} bpm for an adult at rest ({heart.Source})."),
            string.Create(CultureInfo.InvariantCulture,
                $"- Blood oxygen: {oxygen.Low:0}-{oxygen.High:0}% ({oxygen.Source})."),
            $"- Breathing while asleep: {HealthReferenceRanges.NoOvernightBreathingBand}",
            $"- Heart rate variability: {HealthReferenceRanges.NoHeartRateVariabilityBand}",
            "- Steps: no published range; compared against the member's own usual only.");
    }

    /// <summary>The rule and the member's ranges together — what a clinical prompt carries.</summary>
    public static string Block(int? ageYears) => Ranges(ageYears) + "\n" + Rule;
}
