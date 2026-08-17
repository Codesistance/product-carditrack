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
    /// The evening half of the night, which no threshold covered until the pipeline's own call
    /// volume made the gap visible. Pairs with <see cref="DigestDayProgress.IsBeforeWake"/>:
    /// between them the two cover bedtime through to waking, and neither claims the small hours
    /// the other owns.
    /// </summary>
    [Theory]
    [InlineData(12, 0, false)]
    [InlineData(21, 59, false)]
    [InlineData(22, 0, true)]     // the boundary itself: bedtime counts as past it
    [InlineData(23, 59, true)]
    [InlineData(3, 0, false)]     // the small hours are IsBeforeWake's, not this one's
    [InlineData(6, 59, false)]
    public void IsAfterBedtime_CoversBedtimeToMidnight(int hour, int minute, bool expected)
    {
        Assert.Equal(expected, DigestDayProgress.For(Local(hour, minute), baseline: null).IsAfterBedtime);
    }

    /// <summary>
    /// A baseline bedtime past midnight — a night worker, or a device that reads sleep as starting
    /// after 00:00 — has no evening stretch on this side of midnight to gate. Treating 23:00 as
    /// "after a 01:00 bedtime" would silence the one member most likely to still be up.
    /// </summary>
    [Fact]
    public void IsAfterBedtime_IsFalse_WhenBedtimeWrapsPastMidnight()
    {
        var progress = DigestDayProgress.For(
            Local(23), Baseline(wake: new TimeOnly(07, 00), bed: new TimeOnly(01, 00)));

        Assert.False(progress.IsAfterBedtime);
    }

    /// <summary>A member's own early bedtime moves the window with them.</summary>
    [Fact]
    public void IsAfterBedtime_FollowsTheMembersOwnBedtime()
    {
        var baseline = Baseline(wake: new TimeOnly(06, 00), bed: new TimeOnly(20, 00));

        Assert.False(DigestDayProgress.For(Local(19, 30), baseline).IsAfterBedtime);
        Assert.True(DigestDayProgress.For(Local(20, 30), baseline).IsAfterBedtime);
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
    /// Baseline wake/bed are UTC clock faces (<c>BaselineCalculator</c>). Against a London member
    /// at 07:14 BST, a stored 06:00 UTC wake is their actual 07:00 local wake — without converting
    /// through the zone, the class would think they had already been up an hour.
    /// </summary>
    [Fact]
    public void ConvertsBaselineWakeTimesFromUtc_IntoTheMembersLocalClock()
    {
        var london = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
        // 07:14 BST on a mid-summer morning = 06:14 UTC; baseline wake stored as 06:00 UTC.
        var localNow = Local(07, 14);
        var progress = DigestDayProgress.For(
            localNow,
            Baseline(wake: new TimeOnly(06, 00), bed: new TimeOnly(21, 00)),
            london);

        Assert.Equal(new TimeOnly(07, 00), progress.WakeTime);
        Assert.Equal(new TimeOnly(22, 00), progress.Bedtime);
        Assert.Equal(14.0 / 60.0, progress.HoursSinceWake, precision: 2);
        Assert.True(progress.IsEarlyInTheDay);
    }

    /// <summary>
    /// Without a zone the stored faces are used as-is — the path unit fixtures that already speak
    /// in local hours take, and the only safe stance when the anchor cannot be resolved.
    /// </summary>
    [Fact]
    public void LeavesBaselineTimesAsWallClocks_WhenNoZoneIsGiven()
    {
        var progress = DigestDayProgress.For(
            Local(12), Baseline(wake: new TimeOnly(05, 30), bed: new TimeOnly(21, 30)));

        Assert.Equal(new TimeOnly(05, 30), progress.WakeTime);
        Assert.Equal(6.5, progress.HoursSinceWake, precision: 2);
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
