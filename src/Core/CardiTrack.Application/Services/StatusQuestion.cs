using System.Text.RegularExpressions;

namespace CardiTrack.Application.Services;

/// <summary>
/// The readings a status question can name. Ordered as a question is tested for them: the
/// compound names first, so "heart rate variability" is not read as a heart-rate question and
/// "overnight breathing" is not read as sleep.
/// </summary>
public enum StatusMetric
{
    HeartRateVariability = 1,
    RestingHeartRate = 2,
    Oxygen = 3,
    BreathingRate = 4,
    Sleep = 5,
    Steps = 6,
}

/// <summary>
/// What a status-routed question asks for, read from its own words in code.
/// </summary>
/// <remarks>
/// <para>
/// This is the rule §5 gives the status rung — "if the question names a registry metric, compute
/// that value; if it names none, serve the stored line" — which was specified and never built: every
/// status question served the caption. "How is his heart rate" was answered "Steps are lower today
/// than yesterday." (observed on dev 2026-09-07), a true sentence about the wrong reading.
/// </para>
/// <para>
/// One vocabulary for every code-side metric match on this platform. <see cref="AdvisePicker"/>
/// keeps its three coarser topics because a suggestion is written per topic, not per reading; the
/// inference citation filter in <see cref="ChatDataRegistry"/> reads its words from here, so a
/// verdict and a status question cannot disagree about what counts as naming the heart. Whole
/// words, for the reason <see cref="AdvisePicker.TopicOf"/> gives: "rest" inside "interested" is
/// not a sleep question.
/// </para>
/// </remarks>
public static partial class StatusQuestion
{
    /// <summary>The reading the question names, or null when it names none.</summary>
    public static StatusMetric? MetricNamed(string question)
    {
        foreach (var metric in Enum.GetValues<StatusMetric>())
        {
            if (WordsFor(metric).IsMatch(question))
                return metric;
        }

        return null;
    }

    /// <summary>
    /// True when the question asks for the readings themselves — "his specific measurements",
    /// "her readings", "the numbers" — rather than how the person is. The caption is the wrong
    /// answer to that: the caregiver asked for the figures the caption summarises.
    /// </summary>
    public static bool AsksForAllReadings(string question) => ReadingsWords().IsMatch(question);

    /// <summary>The words a caregiver or a clinical read uses for one metric.</summary>
    public static Regex WordsFor(StatusMetric metric) => metric switch
    {
        StatusMetric.HeartRateVariability => HrvWords(),
        StatusMetric.RestingHeartRate => HeartRateWords(),
        StatusMetric.Oxygen => OxygenWords(),
        StatusMetric.BreathingRate => BreathingWords(),
        StatusMetric.Sleep => SleepWords(),
        StatusMetric.Steps => StepsWords(),
        _ => throw new ArgumentOutOfRangeException(nameof(metric), metric, "No word list for this metric."),
    };

    [GeneratedRegex(@"\b(?:hrv|heart rate variability|variability)\b", RegexOptions.IgnoreCase)]
    private static partial Regex HrvWords();

    // "heart rate variability" is its own metric, so "heart rate" only counts when "variability"
    // does not follow — the two lookaheads are what let this list be tested on its own.
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

    [GeneratedRegex(@"\b(?:measurements?|readings?|numbers?|figures|stats|vitals|data)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ReadingsWords();
}
