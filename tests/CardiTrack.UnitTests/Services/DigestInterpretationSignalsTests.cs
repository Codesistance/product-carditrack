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
        decimal? breathing = null,
        int? sleepMinutes = null,
        int? moderateZoneMinutes = null) => new()
    {
        Date = date,
        Steps = steps,
        RestingHeartRate = restingHr,
        AvgHeartRate = avgHr,
        SpO2Average = spo2,
        BreathingRate = breathing,
        SleepMinutes = sleepMinutes,
        ModerateZoneMinutes = moderateZoneMinutes,
    };

    /// <summary>
    /// A night well short of this member's own usual is a computed observation, so it lands in the
    /// block the prompt tells the model to lead with. It used to sit in the usual-pattern section,
    /// where it competed with the findings inside this block and lost — a member sleeping 2.9 hours
    /// against a usual of seven had summaries that led with heart rate and never named the night.
    /// </summary>
    [Fact]
    public void AShortNight_IsAComputedObservation()
    {
        var section = DigestInterpretationSignals.Section(
            Baseline(),
            today: Log(Today, sleepMinutes: 216),
            yesterday: null,
            Morning);

        Assert.Contains("- Last night: 3.6 hours of sleep (usual 7.0) — well short of their usual.", section);
    }

    [Fact]
    public void ALongNight_IsAlsoWorthSaying()
    {
        var section = DigestInterpretationSignals.Section(
            Baseline(),
            today: Log(Today, sleepMinutes: 620),
            yesterday: null,
            Morning);

        Assert.Contains("- Last night: 10.3 hours of sleep (usual 7.0) — well past their usual.", section);
    }

    /// <summary>
    /// The same threshold the alert engine fires on, so a summary can never soothe over a night it
    /// pages about — and, in the other direction, never make an ordinary night sound like an event.
    /// </summary>
    [Theory]
    [InlineData(420)]
    [InlineData(300)]
    [InlineData(540)]
    public void ANightInsideTheOrdinaryBand_EarnsNoObservation(int sleepMinutes)
    {
        var section = DigestInterpretationSignals.Section(
            Baseline(),
            today: Log(Today, sleepMinutes: sleepMinutes),
            yesterday: null,
            Morning);

        Assert.DoesNotContain("Last night", section);
    }

    [Fact]
    public void NoSleepBaseline_MeansNothingToJudgeTheNightAgainst()
    {
        var baseline = Baseline();
        baseline.AvgSleepMinutes = null;

        var section = DigestInterpretationSignals.Section(
            baseline,
            today: Log(Today, sleepMinutes: 216),
            yesterday: null,
            Morning);

        Assert.DoesNotContain("Last night", section);
    }

    /// <summary>
    /// Sleep is attributed to the civil day it ended on, so last night is today's row. Yesterday's
    /// own sleep figure is the night before last, which is not what the caregiver is being asked to
    /// act on this morning.
    /// </summary>
    [Fact]
    public void OnlyLastNightIsReported_NotTheNightBefore()
    {
        var section = DigestInterpretationSignals.Section(
            Baseline(),
            today: null,
            yesterday: Log(Yesterday, sleepMinutes: 216),
            Morning);

        Assert.DoesNotContain("Last night", section);
    }

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
            + "— these findings on a still day, not a day of walking.",
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
        Assert.Contains("these findings on a still day, not a day of walking.", section);
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
        Assert.Contains("these findings on a still day, not a day of walking.", section);
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

    /// <summary>
    /// The elevated-zone finding weighs raised minutes against the day's steps, and both are
    /// running totals — so on a day still in progress it would read a morning walk against a
    /// morning's step count and call it effort without movement. It is stated for finished days
    /// only, the same line <see cref="DigestInterpretationSignals.IsQuiet"/> draws.
    /// </summary>
    [Fact]
    public void TheElevatedZoneFinding_IsHeldBack_WhileTheDayIsStillRunning()
    {
        var partial = Log(Today, steps: 900, moderateZoneMinutes: 30);

        var duringTheDay = DigestInterpretationSignals.RaisedVitals(Baseline(), partial, complete: false);
        var onceFinished = DigestInterpretationSignals.RaisedVitals(Baseline(), partial, complete: true);

        Assert.DoesNotContain(duringTheDay, line => line.Contains("heart rate raised"));
        Assert.Contains(onceFinished, line => line.Contains("heart rate raised"));
    }

    // The whole section, as the digest builds it: a morning with a walk behind it must not tell a
    // family their heart worked on a day of little movement — the day has barely started.
    [Fact]
    public void Section_SaysNothingAboutEffortWithoutMovement_InTheMorning()
    {
        var section = DigestInterpretationSignals.Section(
            Baseline(),
            today: Log(Today, steps: 900, moderateZoneMinutes: 30),
            yesterday: Log(Yesterday, steps: 6100),
            localNow: Morning);

        Assert.DoesNotContain("heart rate raised", section);
    }
}
