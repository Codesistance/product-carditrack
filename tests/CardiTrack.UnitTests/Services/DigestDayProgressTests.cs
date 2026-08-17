using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// Pins how far into their own day a member is judged to be — the value that decides both what the
/// summary prompts are told about a running total and whether the pipeline pays for another
/// generation. The failure behind it: a 07:14 summary reading a just-woken member's 26 steps as a
/// decline against a 6,000-step usual, and a dozen inferences since midnight spent saying so.
/// </summary>
public class DigestDayProgressTests
{
    private static DateTime Local(int hour, int minute = 0) =>
        new(2026, 8, 17, hour, minute, 0, DateTimeKind.Unspecified);

    private static PatternBaseline Baseline(TimeOnly? wake = null, TimeOnly? bed = null) => new()
    {
        PeriodDays = 30,
        TypicalWakeTime = wake,
        TypicalBedtime = bed,
    };

    [Fact]
    public void FallsBackToTheDefaultHours_WhenTheMemberIsStillBeingLearned()
    {
        var progress = DigestDayProgress.For(Local(12), baseline: null);

        Assert.Equal(DigestDayProgress.DefaultWakeTime, progress.WakeTime);
        Assert.Equal(DigestDayProgress.DefaultBedtime, progress.Bedtime);
    }

    /// <summary>A member's own hours, when the baseline has established them.</summary>
    [Fact]
    public void UsesTheMembersOwnWakingHours_WhenTheBaselineHasThem()
    {
        var progress = DigestDayProgress.For(
            Local(12), Baseline(wake: new TimeOnly(05, 30), bed: new TimeOnly(21, 30)));

        Assert.Equal(new TimeOnly(05, 30), progress.WakeTime);
        Assert.Equal(6.5, progress.HoursSinceWake, precision: 2);
    }

    [Theory]
    [InlineData(3, 0, true)]
    [InlineData(6, 59, true)]
    [InlineData(7, 0, true)]     // the boundary itself: not yet up
    [InlineData(7, 1, false)]
    [InlineData(19, 0, false)]
    public void IsBeforeWake_HoldsUntilTheirUsualWakingTime(int hour, int minute, bool expected)
    {
        Assert.Equal(expected, DigestDayProgress.For(Local(hour, minute), baseline: null).IsBeforeWake);
    }

    /// <summary>
    /// The window the regeneration floor widens in. Someone up three hours has had a morning worth
    /// describing; before that the readings move because the day is filling up from nothing.
    /// </summary>
    [Theory]
    [InlineData(6, true)]        // not up at all is not a day well under way
    [InlineData(8, true)]
    [InlineData(9, true)]        // exactly two hours up
    [InlineData(10, false)]      // three hours up: the boundary
    [InlineData(16, false)]
    public void IsEarlyInTheDay_CoversTheFirstHoursAfterWaking(int hour, bool expected)
    {
        Assert.Equal(expected, DigestDayProgress.For(Local(hour), baseline: null).IsEarlyInTheDay);
    }

    /// <summary>
    /// The number a running total should be read against. On the default 07:00–22:00 day, the
    /// morning that produced the failing screenshot accounts for a couple of percent of it.
    /// </summary>
    [Theory]
    [InlineData(5, 0.0)]         // before waking, clamped rather than negative
    [InlineData(7, 0.0)]
    [InlineData(10, 0.2)]
    [InlineData(14, 7.0 / 15)]
    [InlineData(22, 1.0)]
    [InlineData(23, 1.0)]        // past bedtime, clamped rather than over one
    public void WakingDayElapsed_RunsFromWakingToBedtime(int hour, double expected)
    {
        Assert.Equal(expected, DigestDayProgress.For(Local(hour), baseline: null).WakingDayElapsed, precision: 3);
    }

    /// <summary>
    /// A bedtime past midnight comes back negative against the same civil day, and a baseline that
    /// has somehow recorded one an hour after waking would make every reading past mid-morning read
    /// as a whole day — which is the bug this class exists to prevent, arriving through the data.
    /// </summary>
    [Theory]
    [InlineData(23, 30, 07, 00)]  // an ordinary late bedtime
    [InlineData(00, 30, 07, 00)]  // past midnight: wraps rather than inverting
    [InlineData(08, 00, 07, 00)]  // implausibly short: floored at eight hours
    public void WakingDayElapsed_IsNeverDegenerate(int bedHour, int bedMinute, int wakeHour, int wakeMinute)
    {
        var progress = DigestDayProgress.For(
            Local(11),
            Baseline(new TimeOnly(wakeHour, wakeMinute), new TimeOnly(bedHour, bedMinute)));

        Assert.InRange(progress.WakingDayElapsed, 0, 1);
        // Four hours up, against a waking day of at least eight: never more than half gone.
        Assert.True(progress.WakingDayElapsed <= 0.5);
    }

    /// <summary>
    /// The phrase goes into a prompt, so what it actually says is the contract — a model handed
    /// "partial" and nothing else is the thing being fixed.
    /// </summary>
    [Fact]
    public void Describe_SaysTheClock_TheHoursUp_AndHowMuchOfTheDayIsAccountedFor()
    {
        var described = DigestDayProgress.For(Local(07, 14), baseline: null).Describe();

        Assert.Contains("07:14 local", described);
        Assert.Contains("0.2 hours since their usual waking time of 07:00", described);
        Assert.Contains("2% of their waking day has passed", described);
        Assert.Contains("will keep rising", described);
    }

    [Fact]
    public void Describe_SaysSoOutright_WhenTheDayHasNotStarted()
    {
        var described = DigestDayProgress.For(Local(03, 40), baseline: null).Describe();

        Assert.Contains("03:40 local", described);
        Assert.Contains("before their usual waking time of 07:00", described);
        Assert.Contains("today has barely begun", described);
    }

    /// <summary>
    /// The prompt is model input and a cacheable fixed-prefix construction, so nothing in it may
    /// vary with the host's ambient culture — a European locale would render 0.2 as "0,2".
    /// </summary>
    [Fact]
    public void Describe_IsInvariant_WhateverTheHostCultureIs()
    {
        var original = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Contains("0.2 hours", DigestDayProgress.For(Local(07, 14), baseline: null).Describe());
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }
}
