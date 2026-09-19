using CardiTrack.Application.Services;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// <see cref="MemberChatReplies.ClaimsSettled"/> — the whole-picture claim a Yellow-or-worse
/// hero contradicts, which the inference rung answers by asking the clinical read again with the
/// hero's basis named (and withholding a second settled verdict), never by writing a verdict of
/// its own. Pure: a reply in, a yes or no out.
/// </summary>
public class MemberChatStatusTierTests
{
    /// <summary>The whole-picture claims a Yellow hero contradicts.</summary>
    [Theory]
    [InlineData("Everything looks settled — nothing there needs your attention.")]
    [InlineData("Nothing needs your attention right now.")]
    [InlineData("All looks fine for him this week.")]
    [InlineData("No concerns from the readings.")]
    [InlineData("Nothing stands out.")]
    [InlineData("Things look settled for him this week.")]
    [InlineData("His readings look completely settled and steady, so there is nothing you need to watch out for.")]
    public void ASettledVerdict_IsAClaim(string verdict) =>
        Assert.True(MemberChatReplies.ClaimsSettled(verdict));

    /// <summary>
    /// A negated "settled" is agreement with the hero, not a claim against it, and a verdict that
    /// already raises attention has nothing to re-ask.
    /// </summary>
    [Theory]
    [InlineData("Things aren't settled yet — his steps are well below his usual.")]
    [InlineData("Nothing is settled yet: the low step count is worth keeping an eye on.")]
    [InlineData("This is not settled, and it's worth watching.")]
    [InlineData("It's far from settled today.")]
    [InlineData("Things are not yet settled — his steps are still well below usual.")]
    [InlineData("It's not really settled while his steps sit this low.")]
    [InlineData("His steps are well below his usual today, and that's worth keeping an eye on.")]
    public void ANegatedOrAttentiveVerdict_IsNotAClaim(string verdict) =>
        Assert.False(MemberChatReplies.ClaimsSettled(verdict));
}
