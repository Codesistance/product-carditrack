using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// <see cref="MemberChatReplies.ReconcileWithStatusTier"/> — the code behind the rule that an
/// inference verdict may not read as more settled than the dashboard hero above it. Pure, like
/// the other reply-composition policy beside it: the tier and the line come in, the sentence a
/// caregiver reads comes out.
/// </summary>
public class MemberChatStatusTierTests
{
    private static readonly MemberStatusLine Line = new()
    {
        Headline = "Quieter than usual",
        Message = "Steps are very low today.",
        GeneratedAtUtc = DateTime.UtcNow,
    };

    /// <summary>The whole-picture claims a Yellow hero contradicts, each led by the line.</summary>
    [Theory]
    [InlineData("Everything looks settled — nothing there needs your attention.")]
    [InlineData("Nothing needs your attention right now.")]
    [InlineData("All looks fine for him this week.")]
    [InlineData("No concerns from the readings.")]
    [InlineData("Nothing stands out.")]
    [InlineData("Things look settled for him this week.")]
    public void ASettledVerdict_UnderAYellowHero_IsLedByTheStatusLine(string verdict)
    {
        var reply = MemberChatReplies.ReconcileWithStatusTier(verdict, AlertSeverity.Yellow, Line);

        Assert.StartsWith("Steps are very low today. The dashboard is showing that as worth attention today",
            reply, StringComparison.Ordinal);
        Assert.EndsWith(verdict, reply, StringComparison.Ordinal);
    }

    /// <summary>Orange and Red are worse than Yellow, and the guard is "Yellow or worse".</summary>
    [Theory]
    [InlineData(AlertSeverity.Orange)]
    [InlineData(AlertSeverity.Red)]
    public void WorseTiers_AreHeldToTheSameRule(AlertSeverity tier)
    {
        var reply = MemberChatReplies.ReconcileWithStatusTier("Everything looks settled.", tier, Line);

        Assert.StartsWith("Steps are very low today.", reply, StringComparison.Ordinal);
    }

    /// <summary>
    /// A negated "settled" is agreement with the hero, not a claim against it. Leading these with
    /// the status line would say the same thing twice — once in the app's voice, once in the
    /// model's — which is what the bare-token match did.
    /// </summary>
    [Theory]
    [InlineData("Things aren't settled yet — his steps are well below his usual.")]
    [InlineData("Nothing is settled yet: the low step count is worth keeping an eye on.")]
    [InlineData("This is not settled, and it's worth watching.")]
    [InlineData("It's far from settled today.")]
    [InlineData("Things are not yet settled — his steps are still well below usual.")]
    [InlineData("It's not really settled while his steps sit this low.")]
    public void ANegatedSettled_UnderAYellowHero_IsUnchanged(string verdict) =>
        Assert.Equal(verdict, MemberChatReplies.ReconcileWithStatusTier(verdict, AlertSeverity.Yellow, Line));

    /// <summary>A settled hero and a settled verdict agree — nothing to reconcile.</summary>
    [Fact]
    public void ASettledVerdict_UnderAGreenHero_IsUnchanged()
    {
        const string verdict = "Everything looks settled.";

        Assert.Equal(verdict, MemberChatReplies.ReconcileWithStatusTier(verdict, AlertSeverity.Green, Line));
    }

    /// <summary>
    /// A verdict that already agrees with the hero is not decorated: leading a "worth attention"
    /// reply with the line would say the same thing twice.
    /// </summary>
    [Fact]
    public void AVerdictThatAlreadyRaisesAttention_IsUnchanged()
    {
        const string verdict = "His steps are well below his usual today, and that's worth keeping an eye on.";

        Assert.Equal(verdict, MemberChatReplies.ReconcileWithStatusTier(verdict, AlertSeverity.Yellow, Line));
    }

    /// <summary>
    /// A Yellow hero with no current line still corrects the verdict — the tier is the claim
    /// the family is looking at, and the line is only its caption.
    /// </summary>
    [Fact]
    public void AYellowHeroWithNoLine_StillCorrectsASettledVerdict()
    {
        var reply = MemberChatReplies.ReconcileWithStatusTier("Everything looks settled.", AlertSeverity.Yellow, null);

        Assert.StartsWith("The dashboard is showing something worth attention today", reply, StringComparison.Ordinal);
        Assert.EndsWith("Everything looks settled.", reply, StringComparison.Ordinal);
    }
}
