using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// One predicate, three readers. What it pins is the asymmetry that is easy to lose in a later
/// edit: a reading of the current picture goes quiet when it is old, and an explanation of one
/// past event does not.
/// </summary>
public class InsightServabilityTests
{
    private static readonly DateTime Now = new(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void AFreshMemberScopedInsightIsServed()
    {
        Assert.True(InsightServability.IsServable(
            Insight(InsightScope.Baseline, Now.AddHours(-2)), Now));
    }

    [Fact]
    public void AStaleMemberScopedInsightIsWithheld()
    {
        // It describes a picture that has moved on, and there is no way to say so on the row.
        Assert.False(InsightServability.IsServable(
            Insight(InsightScope.Trend, Now - InsightServability.MaxAge - TimeSpan.FromMinutes(1)), Now));
    }

    [Fact]
    public void AnAlertExplanationNeverGoesStale()
    {
        // A caregiver opening a three-week-old alert from their history is exactly who most needs
        // to be told why it fired. The alert happened at a fixed moment; the explanation of it
        // does not age the way a reading of the current picture does.
        var old = Insight(InsightScope.Alert, Now.AddDays(-21));
        old.AlertId = Guid.NewGuid();

        Assert.True(InsightServability.IsServable(old, Now));
    }

    [Fact]
    public void AHorizonsCeilingOutlastsItsOwnCadence()
    {
        // The bug this exists to prevent: at the flat three days a weekly narrative would be
        // withheld on four days in seven and a monthly one on twenty-seven in thirty, which reads
        // to a caregiver as the feature not existing rather than as a row that aged out. Each
        // ceiling must therefore clear the longest gap between two of its own passes — seven days
        // for a week, and thirty-one for January into February.
        Assert.True(InsightServability.WeeklyMaxAge > TimeSpan.FromDays(7));
        Assert.True(InsightServability.MonthlyMaxAge > TimeSpan.FromDays(31));

        Assert.True(InsightServability.IsServable(
            Insight(InsightScope.TrendWeekly, Now.AddDays(-7)), Now));
        Assert.True(InsightServability.IsServable(
            Insight(InsightScope.TrendMonthly, Now.AddDays(-31)), Now));
    }

    [Fact]
    public void AHorizonsCeilingStillCatchesGenerationHavingStopped()
    {
        // Each horizon keeps its own cadence plus slack rather than every scope taking the widest,
        // so a pass that has silently stopped is still noticed at each of them.
        Assert.False(InsightServability.IsServable(
            Insight(InsightScope.TrendWeekly, Now - InsightServability.WeeklyMaxAge - TimeSpan.FromMinutes(1)),
            Now));
        Assert.False(InsightServability.IsServable(
            Insight(InsightScope.TrendMonthly, Now - InsightServability.MonthlyMaxAge - TimeSpan.FromMinutes(1)),
            Now));
    }

    [Fact]
    public void TheRollingReadKeepsItsOwnThreeDays()
    {
        // The wider ceilings belong to the horizons whose cadence earned them. Widening the daily
        // pass's would make a stopped job invisible for five weeks instead of three days.
        Assert.Equal(TimeSpan.FromDays(3), InsightServability.MaxAgeFor(InsightScope.Trend));
        Assert.Equal(TimeSpan.FromDays(3), InsightServability.MaxAgeFor(InsightScope.Baseline));
    }

    [Fact]
    public void ARowIsNeverSweptWhileItIsStillTheCurrentOne()
    {
        // The two retention rules have to agree, and they are in different files: a ceiling that
        // outlived InsightRetention.MaxAge would serve rows the Worker's sweep had already taken.
        Assert.True(InsightServability.MonthlyMaxAge < InsightRetention.MaxAge);
        Assert.True(InsightServability.WeeklyMaxAge < InsightRetention.MaxAge);
    }

    [Fact]
    public void NothingIsServedWithoutText()
    {
        Assert.False(InsightServability.IsServable(null, Now));

        var blank = Insight(InsightScope.Baseline, Now);
        blank.Summary = "   ";
        Assert.False(InsightServability.IsServable(blank, Now));
    }

    private static MemberInsight Insight(InsightScope scope, DateTime generatedAtUtc) => new()
    {
        CardiMemberId = Guid.NewGuid(),
        Scope = scope,
        Summary = "Something worth saying.",
        GeneratedAtUtc = generatedAtUtc,
    };
}
