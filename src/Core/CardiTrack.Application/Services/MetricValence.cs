using System.Globalization;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Services;

/// <summary>
/// How a movement is drawn for a caregiver: favourably, neutrally, or as something to look at.
/// </summary>
/// <remarks>
/// About the <em>movement</em>, never about the person. "Worth attention" says this figure has
/// left a published range or moved the way this metric is conventionally read as worsening; it
/// does not say what will happen next, how likely anything is, or that anyone is unwell. That
/// boundary is the one <c>docs/llm_design.md</c> draws and <c>docs/compliance/dpia.md</c> OI-2
/// turns on, and it is why there is no "at risk" value here and no severity ordering beyond these
/// three.
/// </remarks>
public enum MovementValence
{
    /// <summary>Moved the way this metric is read as improving.</summary>
    Favourable = 1,

    /// <summary>Moved, but not in a direction anything published or conventional calls worse.</summary>
    Neutral = 2,

    /// <summary>Left a published range, or moved the way this metric is read as worsening.</summary>
    WorthAttention = 3,
}

/// <summary>
/// One movement graded, with the grounds it was graded on and the alarm that would watch it.
/// </summary>
/// <param name="Valence">How to draw it.</param>
/// <param name="Basis">
/// Where the grading came from, in a caregiver's words, for printing under the figures. Always
/// populated: a grade with no stated grounds is an opinion wearing a tick.
/// </param>
/// <param name="Alarm">
/// The alarm metric that watches this reading, or null where none does — see
/// <see cref="MetricValence.AlarmFor"/>.
/// </param>
public sealed record MovementGrading(MovementValence Valence, string Basis, AlarmMetric? Alarm);

/// <summary>
/// Whether a movement is drawn as good news, as nothing much, or as something to look at — decided
/// from a table in this file rather than by a model.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two of these six are graded against a published range. The other four are not, and say so.</b>
/// That split is the whole point of the file. <see cref="HealthReferenceRanges"/> publishes a band
/// for resting heart rate (AHA) and sleep (NSF), and those two are graded by where the figure sits
/// against it — a statement about a published range, attributable to the body that published it.
/// The test itself, <see cref="IsOutside"/>, is shared with the Key Metrics cards, which apply it
/// to a single reading for those two and for blood oxygen (decision 2026-09-25: for these three
/// the published range is what normal means, and the member's own usual is context).
/// </para>
/// <para>
/// No body publishes a band for the other four, and three of those absences are refusals rather
/// than gaps: <see cref="HealthReferenceRanges.NoHeartRateVariabilityBand"/>,
/// <see cref="HealthReferenceRanges.NoOvernightBreathingBand"/>, and the step count that
/// <see cref="HealthReferenceRanges"/> declines to derive from WHO's weekly activity guidance
/// because doing the conversion would be our arithmetic wearing WHO's name. Grading those four
/// therefore means encoding a directional convention of our own — fewer steps, less activity and
/// lower overnight HRV read as worse; a higher overnight breathing rate reads as worse. Each one
/// below says in its <see cref="MovementGrading.Basis"/> that it is comparing against the member's
/// own usual and why no published range applies, so the difference reaches the caregiver rather
/// than being flattened into an identical-looking tick.
/// </para>
/// <para>
/// The conventions are asymmetric where the evidence is. A lower overnight breathing rate is
/// <see cref="MovementValence.Neutral"/> rather than <see cref="MovementValence.Favourable"/>,
/// because nothing supports calling it an improvement — only the upward direction has a
/// conventional reading. Inventing a favourable half for symmetry's sake would be making up the
/// half we have no grounds for.
/// </para>
/// <para>
/// A model never sees this and never contributes to it. The grading is a function of figures the
/// baseline learned and constants checked into this file, so it is reproducible from a row, it
/// shows up in a diff when it changes, and a test can hold every entry to what it claims.
/// </para>
/// </remarks>
public static class MetricValence
{
    /// <summary>
    /// Grade one movement for a member of this age. Age only reaches the sleep band, which is the
    /// one published range here that splits on it.
    /// </summary>
    public static MovementGrading Grade(MetricMovement movement, int ageYears)
    {
        ArgumentNullException.ThrowIfNull(movement);

        var (valence, basis) = movement.Kind switch
        {
            // ── Graded against a published range ───────────────────────────────────────────────
            TrackedMetric.RestingHeartRate => AgainstBand(
                movement,
                HealthReferenceRanges.RestingHeartRate.Low,
                HealthReferenceRanges.RestingHeartRate.High,
                "bpm",
                HealthReferenceRanges.RestingHeartRate.Source),

            TrackedMetric.Sleep => AgainstBand(
                movement,
                HealthReferenceRanges.Sleep(ageYears).Low,
                HealthReferenceRanges.Sleep(ageYears).High,
                "hours",
                HealthReferenceRanges.SleepSource),

            // ── Graded on a convention of ours, stated as such ─────────────────────────────────
            TrackedMetric.Steps => ByDirection(
                movement,
                favourableWhenRising: true,
                "Compared against their own usual: no body publishes a daily step count, and "
                + "converting WHO's weekly activity guidance into one would be our arithmetic."),

            TrackedMetric.ActiveMinutes => ByDirection(
                movement,
                favourableWhenRising: true,
                "Counts moderate and vigorous minutes only, not time spent moving. Compared "
                + "against their own usual: the published guidance is written by the week."),

            TrackedMetric.OvernightHeartRateVariability => ByDirection(
                movement,
                favourableWhenRising: true,
                "Compared against their own usual: " + Lowercase(
                    HealthReferenceRanges.NoHeartRateVariabilityBand)),

            // Only the upward direction has a conventional reading, so the downward one is
            // neutral rather than favourable — see the class remarks.
            TrackedMetric.BreathingAsleep => (
                movement.DeviationPercent > 0
                    ? MovementValence.WorthAttention
                    : MovementValence.Neutral,
                "Compared against their own usual: " + Lowercase(
                    HealthReferenceRanges.NoOvernightBreathingBand)),

            _ => throw new ArgumentOutOfRangeException(
                nameof(movement), movement.Kind, "No valence is defined for this metric."),
        };

        return new MovementGrading(valence, basis, AlarmFor(movement.Kind));
    }

    /// <summary>
    /// The alarm metric that watches the same reading, or null where none does.
    /// </summary>
    /// <remarks>
    /// <see cref="TrackedMetric.ActiveMinutes"/> is the null. It is read from
    /// <c>ActivityLog.ActiveMinutes</c>, and no <see cref="AlarmMetric"/> watches that column —
    /// the nearest, <see cref="AlarmMetric.ElevatedZoneMinutes"/>, is a different measurement
    /// (minutes above the light heart-rate zone, from <c>AvgElevatedZoneMinutes</c>). Offering it
    /// as "an alarm for this" would set a caregiver watching a figure other than the one they were
    /// just shown, so a caller with nothing to pre-select should offer no alarm rather than the
    /// wrong one.
    /// </remarks>
    public static AlarmMetric? AlarmFor(TrackedMetric metric) => metric switch
    {
        TrackedMetric.Steps => AlarmMetric.DailySteps,
        TrackedMetric.RestingHeartRate => AlarmMetric.RestingHeartRate,
        TrackedMetric.Sleep => AlarmMetric.SleepMinutes,
        TrackedMetric.OvernightHeartRateVariability => AlarmMetric.OvernightHeartRateVariability,
        TrackedMetric.BreathingAsleep => AlarmMetric.OvernightBreathingRate,
        TrackedMetric.ActiveMinutes => null,
        _ => null,
    };

    /// <summary>
    /// A starting point for an alarm on this movement, as a share of the member's own usual.
    /// </summary>
    /// <remarks>
    /// The movement's own departure, clamped into what
    /// <see cref="MetricAlarmValidation"/> accepts — "tell me if this drifts as far as it just
    /// has", which is a threshold derived from this member's readings rather than a clinical level
    /// the app has decided on. A suggestion the caregiver edits before saving, never a saved
    /// alarm: <c>MetricAlarmEditPage</c> holds everything until Save for that reason.
    /// </remarks>
    public static decimal SuggestedPercentThreshold(MetricMovement movement)
    {
        ArgumentNullException.ThrowIfNull(movement);

        return Math.Clamp(
            Math.Round(Math.Abs(movement.DeviationPercent), 0),
            MetricAlarmValidation.MinPercentThreshold,
            MetricAlarmValidation.MaxPercentThreshold);
    }

    /// <summary>
    /// Whether a reading sits outside a published band — the one test both a movement's grading
    /// (<see cref="AgainstBand"/>) and a Key Metrics card's status
    /// (<c>MemberInsightsCalculator.BuildMetric</c>) apply, so "outside the published range" can
    /// never mean one thing on a movement card and another on the tile above it.
    /// </summary>
    /// <remarks>
    /// Both ends inclusive, as the bands are published: 60 bpm and 100 bpm are inside AHA's
    /// 60–100, and 7 hours is inside the NSF's 7–9. Takes the band rather than a metric so it
    /// reaches blood oxygen too, which has a published range (WHO 94–100) but no
    /// <see cref="TrackedMetric"/> — a single reading on a card is graded, not a movement.
    /// </remarks>
    public static bool IsOutside(decimal reading, MetricReference band)
    {
        ArgumentNullException.ThrowIfNull(band);
        return reading < band.Low || reading > band.High;
    }

    /// <summary>
    /// Graded by where the recent figure sits against a published band, and where it came from.
    /// </summary>
    /// <remarks>
    /// Three outcomes, and each one is a statement about the band rather than about the person:
    /// outside it is worth a look, moving back inside it from outside is good news, and moving
    /// about within it is neither. A member whose figure was already inside the band and stayed
    /// there has moved — that is why there is a card at all — but nothing published says the place
    /// it moved to is a problem, and drawing that as a concern would be us adding one.
    /// </remarks>
    private static (MovementValence, string) AgainstBand(
        MetricMovement movement, decimal low, decimal high, string unit, string source)
    {
        var band = $"the {Figure(low)}–{Figure(high)} {unit} published for adults ({source})";
        var published = new MetricReference { Low = low, High = high, Source = source };
        var recentInside = !IsOutside(movement.Recent, published);
        var usualInside = !IsOutside(movement.Usual, published);

        if (!recentInside)
        {
            var side = movement.Recent < low ? "Below" : "Above";
            return (MovementValence.WorthAttention, $"{side} {band}.");
        }

        return usualInside
            ? (MovementValence.Neutral, $"Still within {band}.")
            : (MovementValence.Favourable, $"Back within {band}.");
    }

    /// <summary>Graded on this metric's own directional convention, with that convention named.</summary>
    private static (MovementValence, string) ByDirection(
        MetricMovement movement, bool favourableWhenRising, string basis)
    {
        var rising = movement.DeviationPercent > 0;
        return (
            rising == favourableWhenRising
                ? MovementValence.Favourable
                : MovementValence.WorthAttention,
            basis);
    }

    /// <summary>
    /// Opens a sentence that is being spliced onto "Compared against their own usual: ".
    /// </summary>
    private static string Lowercase(string sentence) =>
        sentence.Length > 0 ? char.ToLowerInvariant(sentence[0]) + sentence[1..] : sentence;

    /// <summary>Trailing zeros dropped, matching how the figures beside it are printed.</summary>
    private static string Figure(decimal value) =>
        value.ToString("0.#", CultureInfo.InvariantCulture);
}
