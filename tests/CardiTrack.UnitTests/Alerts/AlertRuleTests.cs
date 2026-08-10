using CardiTrack.Application.Services.Alerts;
using CardiTrack.Application.Services.Alerts.Rules;
using CardiTrack.Domain.Enums;
using Xunit;

namespace CardiTrack.UnitTests.Alerts;

/// <summary>
/// The five launch alert types, asserted at their boundaries.
/// </summary>
/// <remarks>
/// Weighted deliberately toward the cases that must <em>not</em> fire. The stated target is under
/// 10% false positives, and an alert that turns out to be nothing costs more trust than a missed
/// one earns — so most of what follows is about silence.
/// </remarks>
public class AlertRuleTests
{
    // ---------------------------------------------------------------- healthy member

    [Fact]
    public void NoRuleFiresForAHealthyWellObservedMember()
    {
        var context = new AlertContextBuilder().Build();

        foreach (var rule in AlertGenerator.Rules)
            Assert.False(rule.Evaluate(context).ShouldAlert, $"{rule.AlertType} fired on a healthy member.");
    }

    // ---------------------------------------------------------------- Inactivity

    [Theory]
    [InlineData(1, false)]  // one quiet day is a rainy Sunday
    [InlineData(2, true)]
    public void Inactivity_NeedsTwoConsecutiveQuietDays(int quietDays, bool expected)
    {
        var context = new AlertContextBuilder()
            .LastDays(quietDays, d => d with { Steps = 1000 })
            .Build();

        Assert.Equal(expected, new InactivityRule().Evaluate(context).ShouldAlert);
    }

    [Theory]
    [InlineData(2999, true)]   // below 50% of a 6000 baseline
    [InlineData(3000, false)]
    public void Inactivity_TripsAtHalfTheUsualStepCount(int steps, bool expected)
    {
        var context = new AlertContextBuilder()
            .LastDays(2, d => d with { Steps = steps })
            .Build();

        Assert.Equal(expected, new InactivityRule().Evaluate(context).ShouldAlert);
    }

    [Fact]
    public void Inactivity_TreatsMissingDaysAsUnknownRatherThanQuiet()
    {
        // A watch left on charge must never read as a person who stopped moving.
        var context = new AlertContextBuilder().MissingLastDays(2).Build();

        Assert.False(new InactivityRule().Evaluate(context).ShouldAlert);
    }

    [Fact]
    public void Inactivity_SaysNothingWithoutAStepBaseline()
    {
        var context = new AlertContextBuilder().NoBaseline().Build();
        Assert.False(new InactivityRule().Evaluate(context).ShouldAlert);
    }

    // ---------------------------------------------------------------- Elevated heart rate

    [Theory]
    [InlineData(2, false)]
    [InlineData(3, true)]
    public void ElevatedHeartRate_NeedsThreeConsecutiveDays(int days, bool expected)
    {
        var context = new AlertContextBuilder()
            .LastDays(days, d => d with { RestingHeartRate = 80 })
            .Build();

        Assert.Equal(expected, new ElevatedHeartRateRule().Evaluate(context).ShouldAlert);
    }

    [Theory]
    [InlineData(71, false)]  // 62 baseline + 15% = 71.3, so 71 is under the bar
    [InlineData(72, true)]
    public void ElevatedHeartRate_TripsFifteenPercentAboveBaseline(int bpm, bool expected)
    {
        var context = new AlertContextBuilder()
            .LastDays(3, d => d with { RestingHeartRate = bpm })
            .Build();

        Assert.Equal(expected, new ElevatedHeartRateRule().Evaluate(context).ShouldAlert);
    }

    // ---------------------------------------------------------------- Sleep

    [Theory]
    [InlineData(4, false)]
    [InlineData(5, true)]
    public void IrregularSleep_NeedsFiveBrokenNights(int nights, bool expected)
    {
        var context = new AlertContextBuilder()
            .LastDays(nights, d => d with { SleepEfficiency = 60 })
            .Build();

        Assert.Equal(expected, new IrregularSleepRule().Evaluate(context).ShouldAlert);
    }

    [Fact]
    public void IrregularSleep_SaysNothingWhenTheWatchIsNotWornOvernight()
    {
        // No sleep baseline means we have never reliably measured their sleep, so five nights of
        // poor readings are more likely five nights of a watch on the bedside table.
        var context = new AlertContextBuilder()
            .NoSleepBaseline()
            .LastDays(5, d => d with { SleepEfficiency = 55 })
            .Build();

        Assert.False(new IrregularSleepRule().Evaluate(context).ShouldAlert);
    }

    // ---------------------------------------------------------------- No morning activity (red)

    [Fact]
    public void NoMorningActivity_FiresWhenTheWatchIsReportingAndShowsNoMovement()
    {
        var context = new AlertContextBuilder().MorningHours(hoursReported: 5, stepsPerHour: 2).Build();

        var verdict = new NoMorningActivityRule().Evaluate(context);

        Assert.True(verdict.ShouldAlert);
        Assert.Equal(AlertSeverity.Red, new NoMorningActivityRule().Severity);
    }

    [Fact]
    public void NoMorningActivity_StaysSilentWhenThereIsNoHourlyDataAtAll()
    {
        // The single most dangerous false positive available: a flat battery must not be reported
        // as a person who has not moved.
        var context = new AlertContextBuilder().NoHourlyData().Build();

        Assert.False(new NoMorningActivityRule().Evaluate(context).ShouldAlert);
    }

    [Theory]
    [InlineData(2, false)]  // too little of the morning reported to conclude anything
    [InlineData(3, true)]
    public void NoMorningActivity_NeedsEnoughOfTheMorningReported(int hoursReported, bool expected)
    {
        var context = new AlertContextBuilder().MorningHours(hoursReported, stepsPerHour: 1).Build();

        Assert.Equal(expected, new NoMorningActivityRule().Evaluate(context).ShouldAlert);
    }

    [Fact]
    public void NoMorningActivity_IgnoresTheStrayCountsOfAWatchBeingPickedUp()
    {
        var context = new AlertContextBuilder().MorningHours(hoursReported: 5, stepsPerHour: 25).Build();

        // 125 steps across the morning clears the movement floor — barely, and deliberately.
        Assert.False(new NoMorningActivityRule().Evaluate(context).ShouldAlert);
    }

    [Fact]
    public void NoMorningActivity_StaysSilentOnTheUtcDefault()
    {
        // "By 11am" against UTC is 3am for a Los Angeles caregiver. Better to say nothing than to
        // judge the wrong part of the day.
        var context = new AlertContextBuilder()
            .TimeZone("UTC")
            .MorningHours(hoursReported: 5, stepsPerHour: 1)
            .Build();

        Assert.False(new NoMorningActivityRule().Evaluate(context).ShouldAlert);
    }

    [Fact]
    public void NoMorningActivity_StaysSilentOnAnUnrecognisedZone()
    {
        var context = new AlertContextBuilder()
            .TimeZone("Mars/Olympus_Mons")
            .MorningHours(hoursReported: 5, stepsPerHour: 1)
            .Build();

        Assert.False(new NoMorningActivityRule().Evaluate(context).ShouldAlert);
    }

    [Fact]
    public void NoMorningActivity_WaitsUntilTheMorningIsActuallyOver()
    {
        // 12:00 UTC is 05:00 in New York — far too early to conclude anything.
        var context = new AlertContextBuilder()
            .TimeZone("America/New_York")
            .MorningHours(hoursReported: 4, stepsPerHour: 1)
            .Build();

        Assert.False(new NoMorningActivityRule().Evaluate(context).ShouldAlert);
    }

    // ---------------------------------------------------------------- Trend

    [Fact]
    public void DecliningTrend_FiresOnASustainedFallBetweenFortnights()
    {
        var context = new AlertContextBuilder()
            .LastDays(14, d => d with { Steps = 4000 })
            .Build();

        Assert.True(new DecliningTrendRule().Evaluate(context).ShouldAlert);
    }

    [Fact]
    public void DecliningTrend_IgnoresADipTooSmallToMeanAnything()
    {
        var context = new AlertContextBuilder()
            .LastDays(14, d => d with { Steps = 5400 })  // a 10% fall
            .Build();

        Assert.False(new DecliningTrendRule().Evaluate(context).ShouldAlert);
    }

    // ---------------------------------------------------------------- Sensitivity

    [Fact]
    public void SensitivityMovesTheThreshold()
    {
        // 3500 steps against a 6000 baseline. The default bar is half of baseline — 3000 — so
        // this is not enough; a family who asked to hear more crosses at 4000 and does.
        AlertContext WithSteps(AlertSensitivity sensitivity) => new AlertContextBuilder()
            .Sensitivity(sensitivity)
            .LastDays(2, d => d with { Steps = 3500 })
            .Build();

        var rule = new InactivityRule();

        Assert.False(rule.Evaluate(WithSteps(AlertSensitivity.Medium)).ShouldAlert);
        Assert.True(rule.Evaluate(WithSteps(AlertSensitivity.High)).ShouldAlert);
    }
}
