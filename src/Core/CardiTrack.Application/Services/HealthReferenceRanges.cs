using CardiTrack.Application.DTOs.Responses;

namespace CardiTrack.Application.Services;

/// <summary>
/// Where a reading sits relative to a published range. <see cref="Unknown"/> is the answer when
/// there is no range or no reading, and is deliberately not <see cref="Within"/>: "nothing to
/// compare" and "compared and fine" must never read the same to a caller deciding what to say.
/// </summary>
public enum BandPosition
{
    Unknown,
    Below,
    Within,
    Above,
}

/// <summary>
/// The published typical-adult range for each Key Metric — the population normal a client draws
/// behind the series next to this member's own learned baseline
/// (<see cref="DashboardMetric.Baseline"/>).
/// </summary>
/// <remarks>
/// <para>
/// These ranges are <b>presentational by default</b>: a client draws them behind a series, and the
/// status colouring next to them stays relative to the member's own baseline. Three call sites are
/// allowed to read a verdict off them, and each is a place where the member's own normal provably
/// cannot see the thing a caregiver is watching for — <see cref="StatisticalAlertRules.IrregularSleep"/>
/// and <see cref="StatisticalAlertRules.ElevatedHeartRate"/> grading the severity of a departure
/// they detected on their own, <c>MemberInsightsCalculator.CapAtRecommendedSleep</c> holding a
/// rating down, and <c>DigestInterpretationSignals</c> naming where a reading landed. None of them
/// may <em>raise</em> an assessment the member's own data did not already earn, and none of them
/// names a reading abnormal: they quote a recommendation and say who published it.
/// </para>
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

    /// <summary>
    /// Where <paramref name="value"/> sits against <paramref name="reference"/>. Both bounds are
    /// inclusive, so a reading exactly on a published figure is inside the recommendation rather
    /// than outside it — the bands are quoted as "60 to 100", and a caregiver told 100 bpm is past
    /// the range would be reading a boundary artefact as a finding.
    /// </summary>
    public static BandPosition Position(MetricReference? reference, decimal? value) =>
        (reference, value) switch
        {
            (null, _) or (_, null) => BandPosition.Unknown,
            var (band, reading) when reading < band!.Low => BandPosition.Below,
            var (band, reading) when reading > band!.High => BandPosition.Above,
            _ => BandPosition.Within,
        };

    /// <summary>
    /// The clause that says where a reading landed against a published band, or null when it landed
    /// inside one (or there was nothing to compare) — a caller appends it to copy that has already
    /// said what the reading was and how it compares with the member's own usual.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Null for <see cref="BandPosition.Within"/> rather than a reassuring clause, and the reason is
    /// the same one <c>DigestInterpretationSignals.Section</c> returns empty on a calm member: a
    /// sentence saying a reading is inside the recommended range is a sentence a caregiver has to
    /// read on every ordinary day, and the one day it is missing is the day they are least likely
    /// to notice. Callers that need to distinguish "inside the band" from "no band at all" ask
    /// <see cref="Position"/> directly.
    /// </para>
    /// <para>
    /// Always names the publisher, for the reason <see cref="SleepSource"/> exists as a const: these
    /// bands come from three different bodies, and copy that quotes a figure without saying whose it
    /// is invites the reader to assume it is ours. The wording quotes a recommendation and stops
    /// there — it never calls a reading high, low or abnormal, which is a judgement no published
    /// range makes about one person on one day.
    /// </para>
    /// </remarks>
    /// <param name="unit">
    /// The unit as it should read in prose ("bpm", "%"), not as an axis label — this clause lands
    /// mid-sentence in copy a family reads.
    /// </param>
    public static string? BandClause(MetricReference? reference, decimal? value, string unit) =>
        Position(reference, value) is BandPosition.Unknown or BandPosition.Within
            ? null
            : BandPlacement(reference, value, unit);

    /// <summary>
    /// The same clause as <see cref="BandClause"/>, but spoken for a reading inside the band too —
    /// null only when there was nothing to compare. For copy that exists <em>because</em> something
    /// already fired, where "inside the range" is the proportion the finding would otherwise leave
    /// a reader to guess at, rather than a reassurance printed on an ordinary day.
    /// </summary>
    public static string? BandPlacement(MetricReference? reference, decimal? value, string unit)
    {
        var position = Position(reference, value);
        if (position is BandPosition.Unknown)
            return null;

        var span = $"the {reference!.Low:0.#}–{reference.High:0.#} {unit} typical for an adult "
            + $"({reference.Source})";

        return position switch
        {
            BandPosition.Above => $"above {span}",
            BandPosition.Below => $"below {span}",
            _ => $"inside {span}",
        };
    }
}
