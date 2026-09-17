using CardiTrack.Application.Services;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// The yes/no reader that resolves a proposed alert-settings change ahead of every model. Exact
/// phrases only: a message that says more than yes or no is a new instruction and must route.
/// </summary>
public class MemberChatConfirmationTests
{
    [Theory]
    [InlineData("yes")]
    [InlineData("Yes!")]
    [InlineData("  yes please ")]
    [InlineData("OK")]
    [InlineData("okay, do it")]
    [InlineData("go ahead.")]
    [InlineData("Yep")]
    [InlineData("that's right")]
    public void AYes_IsAYes(string message) =>
        Assert.Equal(ConfirmationAnswer.Yes, MemberChatReplies.ReadConfirmation(message));

    [Theory]
    [InlineData("no")]
    [InlineData("No thanks")]
    [InlineData("cancel")]
    [InlineData("never mind")]
    [InlineData("leave it")]
    [InlineData("Don't")]
    public void ANo_IsANo(string message) =>
        Assert.Equal(ConfirmationAnswer.No, MemberChatReplies.ReadConfirmation(message));

    /// <summary>Anything that carries more than the answer is not the answer — "yes but only
    /// at night" is a new instruction, and "no idea, is he ok?" is a question.</summary>
    [Theory]
    [InlineData("yes but only at night")]
    [InlineData("no idea, is he ok?")]
    [InlineData("how did he sleep?")]
    [InlineData("yes and turn off the sleep one too")]
    [InlineData("yes 130")]
    [InlineData("yes1")]
    [InlineData("no 2")]
    [InlineData("")]
    [InlineData("???")]
    public void AnythingElse_IsNotAnAnswer(string message) =>
        Assert.Null(MemberChatReplies.ReadConfirmation(message));
}
