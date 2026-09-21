using CardiTrack.Application.Services;
using CardiTrack.Domain.Enums;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// The grading table, held to what it claims.
/// </summary>
/// <remarks>
/// These tests exist because this file is a clinical position rather than a calculation. Two of
/// the six metrics are graded against a range somebody published; the other four are graded on a
/// convention of ours. What matters is that the difference survives — that the first two cite
/// their source, that the other four say out loud they are comparing against the member's own
/// usual, and that nobody later tidies the asymmetries away.
/// </remarks>
public class MetricValenceTests
{
    private const int Adult = 54;
    private const int OlderAdult = 70;

    // ── Graded against a published range ────────────────────────────────────────────────────────

    [Fact]
    public void AFigureOutsideThePublishedBandIsWorthAttention()
    {
        var grading = MetricValence.Grade(
            Movement(TrackedMetric.RestingHeartRate, recent: 104m, usual: 96m), Adult);

        Assert.Equal(MovementValence.WorthAttention, grading.Valence);
        Assert.Contains("Above", grading.Basis);
        Assert.Contains("60–100", grading.Basis);

        // Attributed to the body that publishes it, never to CardiTrack.
        Assert.Contains("AHA", grading.Basis);
    }

    [Fact]
    public void MovingAboutInsideThePublishedBandIsNeitherGoodNorBad()
    {
        // It moved — that is why there is a card at all — but nothing published says 68 bpm is a
        // problem, and drawing it as a concern would be us adding one on top of the AHA's range.
        var grading = MetricValence.Grade(
            Movement(TrackedMetric.RestingHeartRate, recent: 68m, usual: 74m), Adult);

        Assert.Equal(MovementValence.Neutral, grading.Valence);
        Assert.Contains("Still within", grading.Basis);
    }

    [Fact]
    public void ComingBackInsideThePublishedBandIsGoodNews()
    {
        var grading = MetricValence.Grade(
            Movement(TrackedMetric.RestingHeartRate, recent: 92m, usual: 104m), Adult);

        Assert.Equal(MovementValence.Favourable, grading.Valence);
        Assert.Contains("Back within", grading.Basis);
    }

    [Fact]
    public void TheSleepBandFollowsTheMembersAge()
    {
        // 8.5 hours is inside the NSF's 7–9 for an adult and above the 7–8 it publishes from 65,
        // so the same night is graded differently for two people — which is the age split being
        // honoured rather than one band being applied to everybody.
        var night = Movement(TrackedMetric.Sleep, recent: 8.5m, usual: 7.2m);

        Assert.Equal(MovementValence.Neutral, MetricValence.Grade(night, Adult).Valence);
        Assert.Equal(MovementValence.WorthAttention, MetricValence.Grade(night, OlderAdult).Valence);

        Assert.Contains("7–9", MetricValence.Grade(night, Adult).Basis);
        Assert.Contains("7–8", MetricValence.Grade(night, OlderAdult).Basis);
    }

    // ── Graded on a convention of ours, and saying so ───────────────────────────────────────────

    [Theory]
    [InlineData(TrackedMetric.Steps)]
    [InlineData(TrackedMetric.ActiveMinutes)]
    [InlineData(TrackedMetric.OvernightHeartRateVariability)]
    [InlineData(TrackedMetric.BreathingAsleep)]
    public void AMetricWithNoPublishedRangeSaysWhatItIsComparedAgainst(TrackedMetric metric)
    {
        // The caregiver-facing difference between "the AHA puts this outside normal" and "this is
        // unlike them lately". Both are worth showing; presenting the second as the first would be
        // borrowing an authority that has not spoken on it.
        var grading = MetricValence.Grade(Movement(metric, recent: 10m, usual: 20m), Adult);

        Assert.Contains("Compared against their own usual", grading.Basis);
    }

    [Fact]
    public void TheStepBasisDoesNotConvertWhoGuidanceIntoAStepCount()
    {
        var grading = MetricValence.Grade(
            Movement(TrackedMetric.Steps, recent: 3100m, usual: 5400m), Adult);

        Assert.Equal(MovementValence.WorthAttention, grading.Valence);
        Assert.DoesNotContain("10,000", grading.Basis);
        Assert.DoesNotContain("10000", grading.Basis);
    }

    [Fact]
    public void MoreStepsThanUsualIsGoodNews()
    {
        var grading = MetricValence.Grade(
            Movement(TrackedMetric.Steps, recent: 7100m, usual: 5400m), Adult);

        Assert.Equal(MovementValence.Favourable, grading.Valence);
    }

    [Fact]
    public void BreathingSlowerAsleepIsNotCalledAnImprovement()
    {
        // The asymmetry, on purpose. A raised overnight breathing rate has a conventional reading;
        // a lowered one does not, and inventing a favourable half so the metric looks symmetrical
        // would be making up the half there are no grounds for.
        var slower = MetricValence.Grade(
            Movement(TrackedMetric.BreathingAsleep, recent: 13m, usual: 15m), Adult);
        var faster = MetricValence.Grade(
            Movement(TrackedMetric.BreathingAsleep, recent: 17m, usual: 15m), Adult);

        Assert.Equal(MovementValence.Neutral, slower.Valence);
        Assert.Equal(MovementValence.WorthAttention, faster.Valence);
    }

    // ── Every metric, and the alarm behind it ───────────────────────────────────────────────────

    [Fact]
    public void EveryTrackedMetricIsGradedAndSaysWhy()
    {
        // A metric added to the calculator without an entry here would otherwise reach a caregiver
        // with an ungrounded tick beside it, or throw on their dashboard.
        foreach (var metric in Enum.GetValues<TrackedMetric>())
        {
            var grading = MetricValence.Grade(Movement(metric, recent: 9m, usual: 10m), Adult);
            Assert.False(
                string.IsNullOrWhiteSpace(grading.Basis),
                $"{metric} is graded without saying what on.");
        }
    }

    [Fact]
    public void ActiveMinutesOffersNoAlarmBecauseNoneWatchesIt()
    {
        // ActivityLog.ActiveMinutes has no AlarmMetric. The nearest, ElevatedZoneMinutes, counts
        // minutes above the light heart-rate zone from a different baseline column — offering it
        // would set a caregiver watching a figure other than the one they were just shown.
        Assert.Null(MetricValence.AlarmFor(TrackedMetric.ActiveMinutes));

        foreach (var metric in Enum.GetValues<TrackedMetric>()
                     .Where(m => m != TrackedMetric.ActiveMinutes))
        {
            Assert.NotNull(MetricValence.AlarmFor(metric));
        }
    }

    [Fact]
    public void TheAlarmForAMetricWatchesTheSameReading()
    {
        Assert.Equal(AlarmMetric.DailySteps, MetricValence.AlarmFor(TrackedMetric.Steps));
        Assert.Equal(
            AlarmMetric.RestingHeartRate, MetricValence.AlarmFor(TrackedMetric.RestingHeartRate));
        Assert.Equal(AlarmMetric.SleepMinutes, MetricValence.AlarmFor(TrackedMetric.Sleep));
        Assert.Equal(
            AlarmMetric.OvernightHeartRateVariability,
            MetricValence.AlarmFor(TrackedMetric.OvernightHeartRateVariability));
        Assert.Equal(
            AlarmMetric.OvernightBreathingRate,
            MetricValence.AlarmFor(TrackedMetric.BreathingAsleep));

        // The daily grain, never the short-window one: a card about a day's steps that offered an
        // alarm on a ten-minute total would compare a short window against a day's baseline.
        Assert.NotEqual(AlarmMetric.Steps, MetricValence.AlarmFor(TrackedMetric.Steps));
    }

    // ── The suggested threshold ─────────────────────────────────────────────────────────────────

    [Fact]
    public void TheSuggestedThresholdIsTheMembersOwnDeparture()
    {
        var movement = Movement(TrackedMetric.Steps, recent: 3100m, usual: 5400m, deviation: -43m);

        Assert.Equal(43m, MetricValence.SuggestedPercentThreshold(movement));
    }

    [Fact]
    public void TheSuggestedThresholdStaysInsideWhatTheAlarmFormAccepts()
    {
        // A member whose usual is near zero can produce a departure of several hundred percent,
        // and a pre-filled figure the form would refuse is worse than no suggestion at all.
        var enormous = Movement(TrackedMetric.Steps, recent: 9000m, usual: 100m, deviation: 8900m);
        var tiny = Movement(TrackedMetric.Steps, recent: 5400m, usual: 5400m, deviation: 0m);

        Assert.Equal(
            MetricAlarmValidation.MaxPercentThreshold,
            MetricValence.SuggestedPercentThreshold(enormous));
        Assert.Equal(
            MetricAlarmValidation.MinPercentThreshold,
            MetricValence.SuggestedPercentThreshold(tiny));
    }

    private static MetricMovement Movement(
        TrackedMetric kind, decimal recent, decimal usual, decimal? deviation = null) =>
        new(
            kind,
            kind.ToString(),
            "units",
            recent,
            usual,
            deviation ?? Math.Round((recent - usual) / usual * 100m, 0),
            MeasuredDays: 7);
}
