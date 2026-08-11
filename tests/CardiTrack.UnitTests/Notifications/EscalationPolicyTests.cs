using CardiTrack.Application.Services.Notifications;
using CardiTrack.Domain.Enums;
using Xunit;

namespace CardiTrack.UnitTests.Notifications;

/// <summary>
/// Boundary tests for the 120s/300s/900s escalation ladder (§6.3) — the exact moment each step
/// fires matters as much as the fact that it fires at all.
/// </summary>
public class EscalationPolicyTests
{
    private static readonly DateTime SentAt = new(2026, 8, 11, 12, 0, 0, DateTimeKind.Utc);

    private static EscalationContext Context(TimeSpan elapsed, EscalationStage stage, bool escalates = true) => new()
    {
        UtcNow = SentAt + elapsed,
        Escalates = escalates,
        CurrentStage = stage,
        SentDate = SentAt
    };

    [Fact]
    public void NonEscalatingCategory_NeverActs()
    {
        var action = EscalationPolicy.Evaluate(
            Context(TimeSpan.FromMinutes(30), EscalationStage.Initial, escalates: false));

        Assert.Equal(EscalationAction.None, action);
    }

    [Fact]
    public void NoSentDate_NeverActs()
    {
        var context = new EscalationContext
        {
            UtcNow = SentAt,
            Escalates = true,
            CurrentStage = EscalationStage.Initial,
            SentDate = null
        };

        Assert.Equal(EscalationAction.None, EscalationPolicy.Evaluate(context));
    }

    [Theory]
    [InlineData(119, EscalationAction.None)]
    [InlineData(120, EscalationAction.Repush)]
    [InlineData(200, EscalationAction.Repush)]
    public void InitialStage_RepushesAt120Seconds(int elapsedSeconds, EscalationAction expected)
    {
        var action = EscalationPolicy.Evaluate(
            Context(TimeSpan.FromSeconds(elapsedSeconds), EscalationStage.Initial));

        Assert.Equal(expected, action);
    }

    [Theory]
    [InlineData(299, EscalationAction.Repush)]
    [InlineData(300, EscalationAction.FanOutToOtherCaregivers)]
    public void InitialStage_FansOutAt300SecondsIfStillUnacked(int elapsedSeconds, EscalationAction expected)
    {
        // A row still in Initial at 300s means the 120s repush never actually advanced the
        // stored stage (e.g. the worker crashed between send and persist) — the policy still
        // recognizes the later boundary rather than getting stuck re-pushing forever.
        var action = EscalationPolicy.Evaluate(
            Context(TimeSpan.FromSeconds(elapsedSeconds), EscalationStage.Initial));

        Assert.Equal(expected, action);
    }

    [Theory]
    [InlineData(899, EscalationAction.FanOutToOtherCaregivers)]
    [InlineData(900, EscalationAction.MarkUndeliveredCritical)]
    public void InitialStage_MarksUndeliveredCriticalAt900Seconds(int elapsedSeconds, EscalationAction expected)
    {
        var action = EscalationPolicy.Evaluate(
            Context(TimeSpan.FromSeconds(elapsedSeconds), EscalationStage.Initial));

        Assert.Equal(expected, action);
    }

    [Fact]
    public void RepushedStage_DoesNotRepushAgain_FansOutAt300()
    {
        var beforeFanOut = EscalationPolicy.Evaluate(
            Context(TimeSpan.FromSeconds(299), EscalationStage.Repushed));
        var atFanOut = EscalationPolicy.Evaluate(
            Context(TimeSpan.FromSeconds(300), EscalationStage.Repushed));

        Assert.Equal(EscalationAction.None, beforeFanOut);
        Assert.Equal(EscalationAction.FanOutToOtherCaregivers, atFanOut);
    }

    [Fact]
    public void FannedOutStage_OnlyEverMarksUndeliveredCriticalAt900()
    {
        var before = EscalationPolicy.Evaluate(
            Context(TimeSpan.FromSeconds(899), EscalationStage.FannedOut));
        var at = EscalationPolicy.Evaluate(
            Context(TimeSpan.FromSeconds(900), EscalationStage.FannedOut));

        Assert.Equal(EscalationAction.None, before);
        Assert.Equal(EscalationAction.MarkUndeliveredCritical, at);
    }

    [Fact]
    public void UndeliveredCriticalStage_NeverActsAgain()
    {
        var action = EscalationPolicy.Evaluate(
            Context(TimeSpan.FromDays(1), EscalationStage.UndeliveredCritical));

        Assert.Equal(EscalationAction.None, action);
    }
}
