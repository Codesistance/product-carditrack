using System.Globalization;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// Pins the still-day / raised-vital pairing the family digest is handed, so a raised heart
/// rate with almost no steps cannot be recited as two unrelated figures.
/// </summary>
public class DigestInterpretationSignalsTests
{
    private static readonly DateTime Afternoon = new(2026, 8, 10, 17, 0, 0);
    private static readonly DateTime Morning = new(2026, 8, 10, 6, 30, 0);
    private static readonly DateOnly Yesterday = new(2026, 8, 9);
    private static readonly DateOnly Today = new(2026, 8, 10);

    private static PatternBaseline Baseline() => new()
    {
        PeriodDays = 30,
        AvgSteps = 6000,
        AvgRestingHeartRate = 71,
        StdDevHeartRate = 2.0m,
        AvgSleepMinutes = 420,
        TypicalWakeTime = new TimeOnly(7, 0),
    };

    private static ActivityLog Log(
        DateOnly date,
        int? steps = null,
        int? restingHr = null,
        int? avgHr = null,
        decimal? spo2 = null,
        decimal? breathing = null) => new()
    {
        Date = date,
        Steps = steps,
        RestingHeartRate = restingHr,
        AvgHeartRate = avgHr,
        SpO2Average = spo2,
        BreathingRate = breathing,
    };

    [Fact]
    public void QuietAndRaised_OnACompleteStillDay_WithRestingRateAboveUsual()
    {
        var section = DigestInterpretationSignals.Section(
            Baseline(),
            today: Log(Today, steps: 4350, restingHr: 71),
            yesterday: Log(Yesterday, steps: 1200, restingHr: 88),
            Morning);

        Assert.Contains("--- Computed observations ---", section);
        Assert.Contains(
            "Yesterday: resting heart rate 88 bpm (usual 71) with 1,200 steps (usual 6,000) "
            + "— a raised rate on a still day, not a day of walking.",
            section);
        Assert.DoesNotContain("Today so far", section);
    }

    [Fact]
    public void QuietAndRaised_DoesNotFire_WhenStepsAreLowInTheMorning()
    {
        var section = DigestInterpretationSignals.Section(
            Baseline(),
            today: Log(Today, steps: 900, restingHr: 88),
            yesterday: Log(Yesterday, steps: 5800, restingHr: 71),
            Morning);

        Assert.DoesNotContain("still day", section);
        Assert.DoesNotContain("900 steps", section);
        Assert.Contains("Today so far: resting heart rate 88 bpm (usual 71).", section);
    }

    [Fact]
    public void QuietAndRaised_FiresToday_OnceTheAfternoonMakesTheTotalFair()
    {
        var section = DigestInterpretationSignals.Section(
            Baseline(),
            today: Log(Today, steps: 900, restingHr: 88),
            yesterday: Log(Yesterday, steps: 5800, restingHr: 71),
            Afternoon);

        Assert.Contains("Today so far: resting heart rate 88 bpm (usual 71) with 900 steps (usual 6,000)", section);
        Assert.Contains("a raised rate on a still day, not a day of walking.", section);
        Assert.DoesNotContain("Yesterday:", section);
    }

    [Fact]
    public void QuietAndRaised_DoesNotFire_WhenTheyWalked()
    {
        var section = DigestInterpretationSignals.Section(
            Baseline(),
            today: Log(Today, steps: 6200, restingHr: 88),
            yesterday: Log(Yesterday, steps: 5900, restingHr: 88),
            Afternoon);

        Assert.DoesNotContain("still day", section);
        Assert.Contains("Yesterday: resting heart rate 88 bpm (usual 71).", section);
        Assert.Contains("Today so far: resting heart rate 88 bpm (usual 71).", section);
    }

    [Fact]
    public void QuietAndRaised_UsesAverageHeartRate_WhenRestingIsUnmoved()
    {
        var section = DigestInterpretationSignals.Section(
            Baseline(),
            today: null,
            yesterday: Log(Yesterday, steps: 800, restingHr: 72, avgHr: 95),
            Morning);

        Assert.Contains("average heart rate 95 bpm (usual resting 71)", section);
        Assert.Contains("a raised rate on a still day, not a day of walking.", section);
        Assert.DoesNotContain("resting heart rate 72", section);
    }

    [Fact]
    public void QuietAndRaised_UsesLowOxygen_OnAStillDay()
    {
        var section = DigestInterpretationSignals.Section(
            Baseline(),
            today: null,
            yesterday: Log(Yesterday, steps: 800, restingHr: 71, spo2: 91.0m),
            Morning);

        Assert.Contains("oxygen 91%", section);
        Assert.Contains("800 steps (usual 6,000)", section);
        Assert.Contains("still day", section);
    }

    [Fact]
    public void QuietAndRaised_UsesHighBreathing_OnAStillDay()
    {
        var section = DigestInterpretationSignals.Section(
            Baseline(),
            today: null,
            yesterday: Log(Yesterday, steps: 400, restingHr: 71, breathing: 22m),
            Morning);

        Assert.Contains("breathing 22 breaths/min", section);
        Assert.Contains("still day", section);
    }

    [Fact]
    public void QuietOnly_WhenStepsFellButVitalsDidNot()
    {
        var section = DigestInterpretationSignals.Section(
            Baseline(),
            today: null,
            yesterday: Log(Yesterday, steps: 1200, restingHr: 71),
            Morning);

        Assert.Contains("Yesterday: 1,200 steps (usual 6,000).", section);
        Assert.DoesNotContain("still day", section);
        Assert.DoesNotContain("resting heart rate", section);
    }

    [Fact]
    public void MeasuredZeroPastWake_IsQuiet()
    {
        var section = DigestInterpretationSignals.Section(
            Baseline(),
            today: Log(Today, steps: 0, restingHr: 90),
            yesterday: null,
            Afternoon);

        Assert.Contains("Today so far: resting heart rate 90 bpm (usual 71) with 0 steps (usual 6,000)", section);
        Assert.Contains("still day", section);
    }

    [Fact]
    public void NullSteps_AreNotQuiet()
    {
        var section = DigestInterpretationSignals.Section(
            Baseline(),
            today: Log(Today, steps: null, restingHr: 90),
            yesterday: null,
            Afternoon);

        Assert.Contains("Today so far: resting heart rate 90 bpm (usual 71).", section);
        Assert.DoesNotContain("still day", section);
    }

    [Fact]
    public void Empty_WithoutABaseline()
    {
        Assert.Equal(
            string.Empty,
            DigestInterpretationSignals.Section(
                null,
                today: Log(Today, steps: 0, restingHr: 110),
                yesterday: Log(Yesterday, steps: 200, restingHr: 110),
                Afternoon));
    }

    [Fact]
    public void FormatsEveryFigureInvariantly_WhateverTheHostCulture()
    {
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try
        {
            var section = DigestInterpretationSignals.Section(
                Baseline(),
                today: null,
                yesterday: Log(Yesterday, steps: 1200, restingHr: 88),
                Morning);

            Assert.Contains("1,200 steps (usual 6,000)", section);
            Assert.DoesNotContain("1.200", section);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
