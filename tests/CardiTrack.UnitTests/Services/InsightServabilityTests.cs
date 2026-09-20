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
