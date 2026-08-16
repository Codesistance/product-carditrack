using CardiTrack.Application.DTOs.Responses;

namespace CardiTrack.Application.Services;

/// <summary>
/// The published typical-adult range for each Key Metric — the population normal a client draws
/// behind the series next to this member's own learned baseline
/// (<see cref="DashboardMetric.Baseline"/>).
/// </summary>
/// <remarks>
/// <para>
/// Every range names the body that publishes it, because they do not all come from one. WHO
/// publishes the two it is quoted for here — the SpO2 bands in its pulse oximetry guidance and the
/// adult respiratory rate in its Basic Emergency Care material — but publishes no resting heart
/// rate or sleep duration range, so those are attributed to the bodies that do rather than
/// re-labelled WHO.
/// </para>
/// <para>
/// A metric with no published range gets none: skin temperature is a wearer-relative measurement
/// with no population normal (it compares against the device's own nightly baseline instead), and
/// no standards body publishes a daily step count — the WHO physical activity guidelines are
/// written in minutes of moderate activity per week, and converting those to steps would be our
/// arithmetic wearing WHO's name. Steps keep their goal and baseline, which are this member's own.
/// </para>
/// <para>
/// A CardiMember is validated as being between 18 and 120 years old, so these are adult ranges
/// throughout and no paediatric band — where resting heart rate and breathing rate diverge from
/// the adult figures sharply — can apply. Within that span only sleep has a published age split,
/// and it takes one (see <see cref="Sleep"/>); the others are published as single adult ranges,
/// and narrowing them per member would be our own tailoring wearing the publisher's name. None of
/// the four is published split by sex either.
/// </para>
/// </remarks>
public static class HealthReferenceRanges
{
    /// <summary>Where the National Sleep Foundation's "older adults" band starts.</summary>
    public const int OlderAdultAge = 65;

    /// <summary>
    /// Who publishes the sleep band. Named once because the band now reaches a caregiver by three
    /// routes — <see cref="Sleep"/> on the dashboard card, the alert copy that grades a night
    /// against it, and the alert chart's own legend — and three literals could attribute one
    /// recommendation to three bodies.
    /// </summary>
    public const string SleepSource = "NSF";

    /// <summary>
    /// The floor of the National Sleep Foundation's recommended nightly sleep, in hours. The one
    /// figure of <see cref="Sleep"/> that is the same either side of <see cref="OlderAdultAge"/> —
    /// only the ceiling moves.
    /// </summary>
    public const decimal RecommendedSleepFloorHours = 7m;

    /// <summary>
    /// Normal adult resting heart rate, 60–100 bpm (American Heart Association). One band across
    /// adulthood: an individual's resting heart rate drifts with age and fitness, but the AHA
    /// publishes no age-split range to draw it against.
    /// </summary>
    public static MetricReference RestingHeartRate => new() { Low = 60m, High = 100m, Source = "AHA" };

    /// <summary>
    /// Recommended nightly sleep (National Sleep Foundation): 7–9 hours for adults, and 7–8 for
    /// older adults from <see cref="OlderAdultAge"/>. The one published age split among these
    /// ranges — and the one that matters most here, since most CardiMembers are the wrong side of
    /// it and would otherwise be drawn an hour of headroom the recommendation does not give them.
    /// </summary>
    public static MetricReference Sleep(int ageYears) => new()
    {
        Low = RecommendedSleepFloorHours,
        High = ageYears >= OlderAdultAge ? 8m : 9m,
        Source = SleepSource,
    };

    /// <summary>
    /// Normal blood oxygen saturation, 94–100% at sea level (WHO pulse oximetry guidance, which
    /// puts 90–93% at hypoxaemia and below 90% at severe hypoxaemia). Not age-split: the guidance
    /// reads the same figures for an adult of any age.
    /// </summary>
    public static MetricReference SpO2 => new() { Low = 94m, High = 100m, Source = "WHO" };

    /// <summary>
    /// Normal adult respiratory rate, 12–20 breaths per minute (WHO Basic Emergency Care). WHO's
    /// age-dependent thresholds for this one are paediatric, and a CardiMember is an adult.
    /// </summary>
    public static MetricReference BreathingRate => new() { Low = 12m, High = 20m, Source = "WHO" };
}
