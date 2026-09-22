using CardiTrack.Application.Services.Notifications;
using CardiTrack.Domain.Enums;

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

    // ── An escalated copy never widens the blast radius ─────────────────────────
    //
    // The copy is an ordinary red row in every other respect, so without this it reaches its own
    // t+300s rung and copies itself to everybody else — the original recipient included. Each of
    // those does the same 300 seconds later. A family of four turns three pushes into nine and
    // then twenty-seven, and pages ops once per copy.

    private static EscalationContext Copy(TimeSpan elapsed, EscalationStage stage) => new()
    {
        UtcNow = SentAt + elapsed,
        Escalates = true,
        CurrentStage = stage,
        SentDate = SentAt,
        IsEscalatedCopy = true
    };

    [Theory]
    [InlineData(EscalationStage.Initial)]
    [InlineData(EscalationStage.Repushed)]
    public void AnEscalatedCopy_NeverFansOutAgain(EscalationStage stage)
    {
        var action = EscalationPolicy.Evaluate(Copy(EscalationPolicy.FanOutAfter, stage));

        Assert.NotEqual(EscalationAction.FanOutToOtherCaregivers, action);
        Assert.Equal(EscalationAction.None, action);
    }

    [Fact]
    public void AnEscalatedCopy_StillRePushesAtTheFirstRung()
    {
        // Held to the one rung that would widen the fan-out, not exempted from the ladder. The
        // sibling's own phone still deserves a second push.
        Assert.Equal(
            EscalationAction.Repush,
            EscalationPolicy.Evaluate(Copy(EscalationPolicy.RepushAfter, EscalationStage.Initial)));
    }

    [Theory]
    [InlineData(EscalationStage.Initial)]
    [InlineData(EscalationStage.Repushed)]
    [InlineData(EscalationStage.FannedOut)]
    public void AnEscalatedCopy_StillReachesUndeliveredCritical(EscalationStage stage)
    {
        // The outcome the family most needs to exist. A copy nobody answered means there was no
        // cover, and silently dropping it would report cover that was never there.
        Assert.Equal(
            EscalationAction.MarkUndeliveredCritical,
            EscalationPolicy.Evaluate(Copy(EscalationPolicy.UndeliveredAfter, stage)));
    }

    [Fact]
    public void TheOriginalDelivery_StillFansOut()
    {
        // The regression guard: the exemption must reach copies only, or the rung family sharing
        // exists to make real is switched off for everybody.
        var original = Copy(EscalationPolicy.FanOutAfter, EscalationStage.Initial) with
        {
            IsEscalatedCopy = false
        };

        Assert.Equal(EscalationAction.FanOutToOtherCaregivers, EscalationPolicy.Evaluate(original));
    }
}
