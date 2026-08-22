using CardiTrack.Application.Services;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// The rule that decides whether a family may be told their relative is fine. Every negative case
/// below is a way the app could say "all quiet" while not actually watching — which is the failure
/// mode this whole feature has to be built against, so they are asserted one at a time rather than
/// trusted to the happy path.
/// </summary>
public class QuietStretchTests
{
    private static readonly DateTime Now = new(2026, 8, 22, 9, 0, 0, DateTimeKind.Utc);

    private static QuietStretchContext Context(
        bool paused = false,
        bool baseline = true,
        bool unresolved = false,
        string freshness = "green",
        DateTime? lastAlert = null,
        DateTime? monitoringSince = null) =>
        new()
        {
            UtcNow = Now,
            IsMonitoringPaused = paused,
            HasEstablishedBaseline = baseline,
            HasUnresolvedAlerts = unresolved,
            DataFreshnessTier = freshness,
            LastAlertAtUtc = lastAlert,
            MonitoringSinceUtc = monitoringSince ?? Now.AddDays(-90),
        };

    [Fact]
    public void ReportsTheStretch_WhenTheLastAlertIsOldEnoughAndTheWatchIsRunning()
    {
        var lastAlert = Now.AddDays(-9);

        var verdict = QuietStretch.Evaluate(Context(lastAlert: lastAlert));

        Assert.True(verdict.IsQuiet);
        Assert.Equal(9, verdict.Days);
        Assert.Equal(lastAlert, verdict.SinceUtc);
    }

    [Fact]
    public void MeasuresFromComingUnderWatch_WhenNothingWasEverRaised()
    {
        var since = Now.AddDays(-40);

        var verdict = QuietStretch.Evaluate(Context(lastAlert: null, monitoringSince: since));

        Assert.True(verdict.IsQuiet);
        Assert.Equal(40, verdict.Days);
        Assert.Equal(since, verdict.SinceUtc);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(6)]
    public void SaysNothing_BeforeTheSilenceIsLongEnoughToMean_Anything(int daysAgo)
    {
        var verdict = QuietStretch.Evaluate(Context(lastAlert: Now.AddDays(-daysAgo)));

        Assert.False(verdict.IsQuiet);
        Assert.Equal(0, verdict.Days);
    }

    [Fact]
    public void CountsWholeDaysOnly_SoTheFirstWeeksMessageCannotLandEarly()
    {
        // Six days and 23 hours is not a week, and rounding it up would also let the weekly push
        // fire twice inside one week — see QuietStretch.WeeklyOccurrence.
        var verdict = QuietStretch.Evaluate(Context(lastAlert: Now.AddDays(-7).AddHours(1)));

        Assert.False(verdict.IsQuiet);
    }

    [Fact]
    public void SaysNothing_WhileMonitoringIsPaused()
    {
        // The most dangerous case in the file: a paused member produces no alerts precisely
        // because nobody is looking at them.
        var verdict = QuietStretch.Evaluate(Context(paused: true, lastAlert: Now.AddDays(-30)));

        Assert.False(verdict.IsQuiet);
    }

    [Fact]
    public void SaysNothing_WithoutAnEstablishedBaseline()
    {
        // No 30-day baseline means StatisticalAlertService will not raise a single rule against
        // this member, so their silence is not evidence of anything.
        var verdict = QuietStretch.Evaluate(Context(baseline: false, lastAlert: Now.AddDays(-30)));

        Assert.False(verdict.IsQuiet);
    }

    [Theory]
    [InlineData("amber")]
    [InlineData("red")]
    public void SaysNothing_WhenTheWearableHasStoppedReporting(string freshness)
    {
        var verdict = QuietStretch.Evaluate(Context(freshness: freshness, lastAlert: Now.AddDays(-30)));

        Assert.False(verdict.IsQuiet);
    }

    [Fact]
    public void SaysNothing_WhileSomethingIsStillOpen()
    {
        // An unresolved alert from three weeks ago still makes the stretch itself long — the
        // episode has to be closed before "nothing has come up" is true.
        var verdict = QuietStretch.Evaluate(Context(unresolved: true, lastAlert: Now.AddDays(-21)));

        Assert.False(verdict.IsQuiet);
    }

    [Fact]
    public void SaysNothing_WhenTheAnchorIsInTheFuture()
    {
        // A clock skew or a bad backfill must not produce a negative stretch that reads as quiet.
        var verdict = QuietStretch.Evaluate(Context(lastAlert: Now.AddDays(1)));

        Assert.False(verdict.IsQuiet);
    }

    [Theory]
    [InlineData(7, 1)]
    [InlineData(13, 1)]
    [InlineData(14, 2)]
    [InlineData(29, 4)]
    public void WeeklyOccurrence_ChangesExactlyOncePerWeek(int quietDays, int expected)
    {
        // This is what paces the push: the same week must always produce the same number (so a
        // second sweep that day is deduped) and the next week must produce a different one.
        Assert.Equal(expected, QuietStretch.WeeklyOccurrence(quietDays));
    }
}
