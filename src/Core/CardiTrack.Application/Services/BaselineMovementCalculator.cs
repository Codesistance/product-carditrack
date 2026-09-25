using System.Globalization;
using CardiTrack.Domain.Entities;

namespace CardiTrack.Application.Services;

/// <summary>
/// Which of the six a movement is about, independently of what it is called.
/// </summary>
/// <remarks>
/// The label beside it is presentational — "Breathing rate asleep" is wording a caregiver reads,
/// and wording gets revised. Anything that has to decide something about a metric
/// (<see cref="MetricValence"/> grading it, a client pre-selecting the alarm that watches it)
/// keys off this instead, so a copy edit cannot quietly change which metric a decision was made
/// about.
/// </remarks>
public enum TrackedMetric
{
    Steps = 1,
    RestingHeartRate = 2,
    Sleep = 3,
    ActiveMinutes = 4,
    OvernightHeartRateVariability = 5,
    BreathingAsleep = 6,
}

/// <summary>
/// One metric that has actually moved: where it sits now, what is usual for this member, and how
/// far apart those are.
/// </summary>
/// <param name="Kind">Which metric this is, for anything that has to decide something about it.</param>
/// <param name="Metric">The label a caregiver reads — plain words, not a column name.</param>
/// <param name="Unit">What the two figures are in.</param>
/// <param name="Recent">Mean over the last <see cref="BaselineMovementCalculator.RecentDays"/> measured days.</param>
/// <param name="Usual">The member's own learned figure over the baseline window.</param>
/// <param name="DeviationPercent">Signed whole percent, negative below their usual.</param>
/// <param name="MeasuredDays">How many of the recent days carried a reading for this metric.</param>
public sealed record MetricMovement(
    TrackedMetric Kind,
    string Metric,
    string Unit,
    decimal Recent,
    decimal Usual,
    decimal DeviationPercent,
    int MeasuredDays);

/// <summary>
/// What is worth telling a caregiver about right now, and what was checked and found steady.
/// </summary>
/// <param name="Through">The last day the recent window covers.</param>
/// <param name="BaselinePeriodDays">The window the "usual" figures were learned over.</param>
/// <param name="Notable">Only the metrics that cleared the bar, widest departure first.</param>
/// <param name="Steady">The metrics that were measured and did not clear it, by name.</param>
/// <param name="Unjudged">
/// Metrics this member has a usual for, but too few readings this week to judge against it.
/// </param>
/// <param name="OutsideRange">
/// Sleep and resting heart rate whose week sat outside the published normal range, moved or not —
/// see <see cref="RangePlacement"/>. Never also in <paramref name="Steady"/>: a week outside the
/// range is not "nothing to report" however ordinary it is for them.
/// </param>
public sealed record BaselineMovements(
    DateOnly Through,
    int BaselinePeriodDays,
    IReadOnlyList<MetricMovement> Notable,
    IReadOnlyList<string> Steady,
    IReadOnlyList<string> Unjudged,
    IReadOnlyList<RangePlacement>? OutsideRange = null)
{
    /// <summary><see cref="OutsideRange"/>, never null.</summary>
    public IReadOnlyList<RangePlacement> OutsidePublishedRange => OutsideRange ?? [];

    /// <summary>
    /// Whether there is anything here worth spending a model call on: something moved, or
    /// something sat outside its published range (decision 2026-09-25 — a week outside the range
    /// is worth a card even when it is this member's usual).
    /// </summary>
    public bool HasAnythingToSay => Notable.Count > 0 || OutsidePublishedRange.Count > 0;

    /// <summary>
    /// Whether this week is positive evidence that nothing is off: every metric this member has a
    /// usual for was judged, and not one of them had moved.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The property a standing concern may be retracted on, and deliberately stricter than "we
    /// looked and saw nothing". A week with four heart-rate readings and no step readings has
    /// judged something, but it has said nothing at all about steps — so taking down a card about
    /// this member's steps on the strength of it would be retracting a concern on evidence that
    /// never addressed it. A partial sync outage should leave the card exactly where it is.
    /// </para>
    /// <para>
    /// A metric the baseline never learned a usual for is not part of this: it is not measured for
    /// this member at all, and waiting for it would mean never retracting anything. Where a metric
    /// does have a usual and then stops being reported for good — a change of watch — this stays
    /// false and the row is left to age out of <c>InsightServability</c> instead, which is the
    /// behaviour that existed before it could be removed at all.
    /// </para>
    /// </remarks>
    public bool ShowsNothingIsOff =>
        Notable.Count == 0 && Unjudged.Count == 0 && OutsidePublishedRange.Count == 0 && Steady.Count > 0;
}

/// <summary>
/// One metric's week against its published normal range: what it averaged, and the range it sat
/// outside, already worded.
/// </summary>
/// <param name="Kind">Which metric.</param>
/// <param name="Metric">The label a caregiver reads.</param>
/// <param name="Unit">What <paramref name="Recent"/> is in.</param>
/// <param name="Recent">The week's mean.</param>
/// <param name="Placement">"below the 7-9 hours recommended at their age (NSF)" and the like.</param>
public sealed record RangePlacement(
    TrackedMetric Kind, string Metric, string Unit, decimal Recent, string Placement);

/// <summary>
/// The deterministic half of "how are they doing": which of this member's metrics have moved away
/// from their own usual far enough to be worth a caregiver's attention, decided in .NET before any
/// model sees the readings.
/// </summary>
/// <remarks>
/// <para>
/// This exists because the card it feeds was answering the wrong question. The brief asked the
/// model to "describe this person's health trends" over a JSON fence of seven days by eight
/// metrics, with no bar for what counted — so it produced a readout, one line per metric, in which
/// "resting heart rate remained relatively stable" was a finding. Seven such lines is not an
/// insight into whether anything is wrong; it is the data restated, and a caregiver has to do the
/// judging the product promised to do for them.
/// </para>
/// <para>
/// So the judging moves here, where it can be audited and reproduced, and the model is left the
/// job it is good at: saying in plain words what the figures this class selected mean. That is the
/// same division <see cref="TrendFeatureCalculator"/> draws, and the reason its narratives read as
/// findings while this card's read as a table.
/// </para>
/// <para>
/// <b>The bar is the member's own spread, not a fixed percentage.</b> A steady sleeper moving
/// forty minutes is a change; a restless one moving forty minutes is Tuesday. Where the baseline
/// learned a standard deviation for a metric this uses it, and falls back to a percentage only for
/// the metrics no deviation is stored for — the same "larger of their own variation and a floor"
/// shape <see cref="StatisticalAlertRules"/> uses, at a softer setting, because that one pages
/// somebody and this one writes a sentence.
/// </para>
/// </remarks>
public static class BaselineMovementCalculator
{
    /// <summary>
    /// The window "recently" means. A week, so one odd day cannot carry it — the same span
    /// <see cref="TrendFeatureCalculator.MovingAverageDays"/> uses, for the same reason.
    /// </summary>
    public const int RecentDays = 7;

    /// <summary>
    /// How many of those days must carry a reading before a metric is judged at all. Two days out
    /// of seven is not a week's average, and calling it one would let a single unusual morning
    /// decide whether a caregiver is told something is wrong.
    /// </summary>
    public const int MinimumMeasuredDays = 4;

    /// <summary>
    /// How far from their usual counts, in standard deviations of their own variation.
    /// </summary>
    /// <remarks>
    /// One, where <see cref="StatisticalAlertRules"/> uses two. The alert rules raise something
    /// that interrupts a caregiver's day, so they are deliberately hard to trip; this card is
    /// read when the caregiver has already chosen to look, and its job is to mention a drift
    /// before it becomes the thing that pages them. Two standard deviations here would mean the
    /// card only ever appeared for something already close to alerting, which the alert itself
    /// covers better.
    /// </remarks>
    public const decimal SigmaMultiplier = 1m;

    /// <summary>
    /// The floor for the metrics that scale with the person, as a fraction of their usual.
    /// </summary>
    /// <remarks>
    /// Half the <see cref="StatisticalAlertRules.DeviationFraction"/> the daily rules fire on,
    /// which keeps the two in the same family without this one shadowing them. It is a floor
    /// rather than the bar: it applies where no spread has been learned, and it catches the member
    /// whose spread is so narrow that one deviation of it is a rounding error.
    /// </remarks>
    public const decimal MinimumFraction = 0.15m;

    /// <summary>
    /// The floor for resting heart rate, in bpm, because a fraction is the wrong shape for it.
    /// </summary>
    /// <remarks>
    /// Fifteen percent of a resting heart rate is about ten beats, which is most of the adult
    /// range — a floor that size is one nothing ever clears. Absolute for the same reason
    /// <see cref="StatisticalAlertRules.HrMarginFloorBpm"/> is, and softer than its five, because
    /// that one interrupts a caregiver's day and this one writes them a sentence.
    /// </remarks>
    public const decimal HeartRateFloorBpm = 3m;

    /// <summary>
    /// The floor for breathing asleep, per minute.
    /// </summary>
    /// <remarks>
    /// Absolute for the reason <see cref="StatisticalAlertRules.BreathingMarginFloorPerMinute"/>
    /// gives: respiratory rate does not span between people the way RMSSD does, so a breath a
    /// minute means the same thing at 13 as at 17. Half that rule's one, on the same softer
    /// setting as the rest of this class.
    /// </remarks>
    public const decimal BreathingFloorPerMinute = 0.5m;

    /// <summary>
    /// The floor for overnight heart rate variability, as a fraction of their usual.
    /// </summary>
    /// <remarks>
    /// Fractional rather than absolute, and deliberately: RMSSD spans an order of magnitude
    /// between healthy adults, so a fixed millisecond floor is untrippable for one and permanently
    /// tripped for the next — the reasoning
    /// <see cref="HealthReferenceRanges.NoHeartRateVariabilityBand"/> gives for publishing no band
    /// for it at all. A fraction scales with whoever is being measured, which is the property
    /// wanted here.
    /// </remarks>
    public const decimal HeartRateVariabilityFraction = 0.10m;

    /// <summary>
    /// What has moved for this member, or null when there is no established baseline to measure
    /// against — the learning state, which is a different card and a different brief.
    /// </summary>
    /// <param name="ageYears">
    /// Picks the sleep range's ceiling. Null still judges the seven-hour floor, which holds at every
    /// adult age, and never a ceiling it cannot place.
    /// </param>
    public static BaselineMovements? Compute(
        IReadOnlyList<ActivityLog> logs, PatternBaseline? baseline, DateOnly through, int? ageYears = null)
    {
        if (baseline is null)
            return null;

        // One row per date before anything is averaged. Ingestion upserts per
        // (DeviceConnection, Date), so a member wearing two watches has two rows for the same day,
        // and a mean over raw rows weights those days double. The most recently written row per
        // date is the rule BaselineCalculator uses, so the figures here and the usual they are
        // compared against are drawn the same way.
        var from = through.AddDays(-(RecentDays - 1));
        var days = logs
            .Where(l => l.Date >= from && l.Date <= through)
            .GroupBy(l => l.Date)
            .Select(g => g.OrderByDescending(l => l.UpdatedDate ?? l.CreatedDate).First())
            .ToList();

        var notable = new List<MetricMovement>();
        var steady = new List<string>();
        var unjudged = new List<string>();
        var outsideRange = new List<RangePlacement>();

        foreach (var metric in Metrics)
        {
            // No learned usual means this metric is not measured for this member at all, so it is
            // not part of the picture and its absence says nothing. That is a different thing from
            // a metric they do have a usual for going unread this week, which is a gap.
            if (metric.Usual(baseline) is not > 0 || metric.Usual(baseline) is not { } usual)
                continue;

            var readings = days.Select(metric.Read).OfType<decimal>().ToList();
            if (readings.Count < MinimumMeasuredDays)
            {
                unjudged.Add(metric.Label);
                continue;
            }

            var recent = Math.Round(readings.Average(), 1);
            var margin = MarginFor(metric, baseline, usual);

            // Placed against the published range before the usual is consulted, and whether or not
            // it moved: a steady week outside the range is still outside it.
            var placement = PlacementFor(metric.Kind, recent, ageYears);
            if (placement is not null)
                outsideRange.Add(new RangePlacement(metric.Kind, metric.Label, metric.Unit, recent, placement));

            // At exactly the margin the metric is steady, not notable: the bar is what a movement
            // has to pass, and StatisticalAlertRules draws the boundary the same way
            // (`restingHr <= average + margin` returns no finding).
            if (Math.Abs(recent - usual) <= margin)
            {
                if (placement is null)
                    steady.Add(metric.Label);
                continue;
            }

            notable.Add(new MetricMovement(
                metric.Kind,
                metric.Label,
                metric.Unit,
                recent,
                Math.Round(usual, 1),
                Math.Round((recent - usual) / usual * 100m, 0),
                readings.Count));
        }

        return new BaselineMovements(
            through,
            baseline.PeriodDays,
            // Widest departure first: where several things have moved, the caregiver's attention
            // should land on the one that moved furthest rather than on whichever metric this
            // file happens to list first.
            [.. notable.OrderByDescending(m => Math.Abs(m.DeviationPercent))],
            steady,
            unjudged,
            outsideRange);
    }

    /// <summary>
    /// Where a week's mean sits against its published normal range, worded, or null when inside it
    /// or when the metric has none. Sleep and resting heart rate only: blood oxygen is not one of
    /// the six this card judges, and breathing asleep, heart rate variability, steps and zone
    /// minutes have no published range (<see cref="PublishedNormal"/>).
    /// </summary>
    private static string? PlacementFor(TrackedMetric kind, decimal recent, int? ageYears)
    {
        switch (kind)
        {
            case TrackedMetric.Sleep:
                var floor = HealthReferenceRanges.RecommendedSleepFloorHours;
                if (ageYears is { } age)
                {
                    var band = HealthReferenceRanges.Sleep(age);
                    if (recent < band.Low || recent > band.High)
                    {
                        return string.Create(CultureInfo.InvariantCulture,
                            $"{(recent < band.Low ? "below" : "above")} the {band.Low:0.#}-{band.High:0.#} hours recommended at their age ({band.Source})");
                    }

                    return null;
                }

                return recent < floor
                    ? string.Create(CultureInfo.InvariantCulture,
                        $"below the {floor:0.#} hours a night recommended for adults ({HealthReferenceRanges.SleepSource})")
                    : null;

            case TrackedMetric.RestingHeartRate:
                var heart = HealthReferenceRanges.RestingHeartRate;
                return recent < heart.Low || recent > heart.High
                    ? string.Create(CultureInfo.InvariantCulture,
                        $"{(recent < heart.Low ? "below" : "above")} the {heart.Low:0}-{heart.High:0} bpm published range ({heart.Source})")
                    : null;

            default:
                return null;
        }
    }

    /// <summary>
    /// How far this metric has to move before it is worth saying: the larger of one standard
    /// deviation of their own variation and the metric's own floor.
    /// </summary>
    /// <remarks>
    /// The floor is per metric rather than one fraction across all of them, because the metrics
    /// are not the same shape. Fifteen percent of a step count is a real change; fifteen percent
    /// of a resting heart rate is ten beats, which is most of the adult range and a bar nothing
    /// clears. The alert rules draw the same distinction, for the same reason.
    /// </remarks>
    private static decimal MarginFor(Metric metric, PatternBaseline baseline, decimal usual)
    {
        var floor = metric.Floor(usual);
        return metric.Spread(baseline) is > 0 and { } spread
            ? Math.Max(spread * SigmaMultiplier, floor)
            : floor;
    }

    /// <summary>
    /// The block as the prompt carries it. Rendered here rather than in the service so the
    /// arithmetic and its wording stay together, and so there is no second place a percentage
    /// could be worked out differently.
    /// </summary>
    public static string Render(BaselineMovements movements)
    {
        var lines = new List<string>
        {
            $"Window: the {RecentDays} days ending "
            + $"{movements.Through.ToString("O", CultureInfo.InvariantCulture)}, against their own "
            + $"usual learned over {movements.BaselinePeriodDays} days.",
            "Moved away from their usual:",
        };

        foreach (var m in movements.Notable)
        {
            var direction = m.DeviationPercent < 0 ? "below" : "above";
            lines.Add(
                $"- {m.Metric}: averaging {Figure(m.Recent)} {m.Unit} over {m.MeasuredDays} measured "
                + $"day(s), against their usual {Figure(m.Usual)}. That is "
                + $"{Math.Abs(m.DeviationPercent):0}% {direction} their usual.");
        }

        // Its own section, and whether or not the metric moved: a week outside the published range
        // is worth attention even when it is this member's usual (decision 2026-09-25).
        if (movements.OutsidePublishedRange.Count > 0)
        {
            lines.Add("Outside the published normal range this week, whatever their usual:");
            foreach (var r in movements.OutsidePublishedRange)
                lines.Add($"- {r.Metric}: averaging {Figure(r.Recent)} {r.Unit}, {r.Placement}.");
        }

        // Named rather than left out, so the model can say the rest is steady without counting
        // anything itself — and cannot imply a metric moved by failing to mention it.
        if (movements.Steady.Count > 0)
            lines.Add("Measured and steady, nothing to report: " + string.Join(", ", movements.Steady) + ".");

        // And the gaps said out loud, for the same reason in reverse: unread is not steady, and a
        // metric missing from both lists would otherwise be a silence the model fills in.
        if (movements.Unjudged.Count > 0)
        {
            lines.Add(
                "Too few readings this week to judge: " + string.Join(", ", movements.Unjudged)
                + ". Say nothing about these.");
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// The sentence a caregiver reads for one movement, in the wording the prompt already uses.
    /// </summary>
    /// <remarks>
    /// Here rather than on the client for the reason <see cref="Render"/> is here: these figures
    /// are rounded and worded in one place, so the block the model is given and the card a
    /// caregiver reads cannot describe the same movement two ways.
    /// </remarks>
    public static string Headline(MetricMovement movement) =>
        $"{movement.Metric}: {Figure(movement.Recent)} {movement.Unit}, "
        + $"against a usual {Figure(movement.Usual)}.";

    /// <summary>Trailing zeros dropped, so 7.0 hours prints as 7 and 6.4 stays 6.4.</summary>
    private static string Figure(decimal value) =>
        value.ToString("0.#", CultureInfo.InvariantCulture);

    /// <summary>One metric: how to read it from a day, from a baseline, and what to call it.</summary>
    /// <param name="Floor">The least movement worth reporting, given what is usual for them.</param>
    private readonly record struct Metric(
        TrackedMetric Kind,
        string Label,
        string Unit,
        Func<ActivityLog, decimal?> Read,
        Func<PatternBaseline, decimal?> Usual,
        Func<PatternBaseline, decimal?> Spread,
        Func<decimal, decimal> Floor);

    /// <summary>
    /// The six metrics this card judges — the same six
    /// <see cref="TrendFeatureCalculator"/> reports, which a test holds the two to.
    /// </summary>
    /// <remarks>
    /// Sleep is carried in hours here where the trend features carry minutes, and deliberately:
    /// these two figures are printed beside each other for a person to read, and 432 is not a
    /// night's sleep to anyone but a database. <c>ReportComparison</c> converts it for the same
    /// reason. Four of the six have a learned spread on the baseline — steps, resting heart rate,
    /// heart rate variability and breathing asleep — so only sleep and active minutes are judged
    /// on their floor alone.
    /// </remarks>
    private static readonly Metric[] Metrics =
    [
        new(TrackedMetric.Steps, "Steps", "steps a day",
            l => l.Steps, b => b.AvgSteps, b => b.StdDevSteps, Fraction),
        new(TrackedMetric.RestingHeartRate, "Resting heart rate", "bpm",
            l => l.RestingHeartRate, b => b.AvgRestingHeartRate, b => b.StdDevHeartRate,
            _ => HeartRateFloorBpm),
        new(TrackedMetric.Sleep, "Sleep", "hours a night",
            l => Hours(l.SleepMinutes), b => Hours(b.AvgSleepMinutes), _ => null, Fraction),
        // Named from ActivityMetricNaming, which explains why this is not "active minutes":
        // the provider counts moderate and vigorous minutes only, so a long gentle walk barely
        // registers and the old name invited a caregiver to read an active member as a still one.
        new(TrackedMetric.ActiveMinutes,
            ActivityMetricNaming.Label,
            ActivityMetricNaming.MinutesPerDayUnit,
            l => l.ActiveMinutes, b => b.AvgActiveMinutes, _ => null, Fraction),
        new(TrackedMetric.OvernightHeartRateVariability, "Overnight heart rate variability", "ms",
            l => l.HeartRateVariabilityMs,
            b => b.AvgHeartRateVariabilityMs,
            b => b.StdDevHeartRateVariability,
            usual => usual * HeartRateVariabilityFraction),
        new(TrackedMetric.BreathingAsleep, "Breathing rate asleep", "breaths a minute",
            l => l.OvernightBreathingRate,
            b => b.AvgOvernightBreathingRate,
            b => b.StdDevOvernightBreathingRate,
            _ => BreathingFloorPerMinute),
    ];

    /// <summary>The default floor: a share of whatever is usual for them.</summary>
    private static decimal Fraction(decimal usual) => usual * MinimumFraction;

    private static decimal? Hours(decimal? minutes) =>
        minutes is { } m ? Math.Round(m / 60m, 1) : null;
}
