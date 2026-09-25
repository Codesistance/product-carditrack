using CardiTrack.Application.Services;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// <see cref="MemberChatReplies.CarriesNoQuestion"/> — the pre-router guard for a message with
/// nothing a question could be made of. Narrow by design: everything the router can already
/// place, it must leave alone.
/// </summary>
public class MemberChatNonQuestionTests
{
    [Theory]
    [InlineData("someone@example.com")]
    [InlineData("  someone@example.com  ")]
    [InlineData("someone@example.com.")]
    [InlineData("https://example.com/some/path?x=1")]
    [InlineData("http://example.com")]
    [InlineData("www.example.com")]
    [InlineData("12345")]
    [InlineData("???")]
    [InlineData("...")]
    [InlineData("👍")]
    public void AnAddressOrALineWithNoWord_CarriesNoQuestion(string message) =>
        Assert.True(MemberChatReplies.CarriesNoQuestion(message));

    /// <summary>Anything with a word in it is the router's to place — a greeting, a terse
    /// follow-up, an address inside a sentence, a question in another script.</summary>
    [Theory]
    [InlineData("hi")]
    [InlineData("ok?")]
    [InlineData("why?")]
    [InlineData("how is dad today")]
    [InlineData("send it to someone@example.com")]
    [InlineData("see https://example.com for the guidance")]
    [InlineData("¿cómo durmió?")]
    [InlineData("he did 4,000 steps")]
    public void AMessageWithAWordInIt_IsLeftToTheRouter(string message) =>
        Assert.False(MemberChatReplies.CarriesNoQuestion(message));

    [Fact]
    public void TheNudgeNamesTheMember_AndWhatToAskInstead()
    {
        Assert.Equal(
            "I didn't quite catch a question there. Ask me how Dad's sleep, activity or heart rate have been, what's behind an alert, or to pull up the journal — and I can switch alerts on or off or set an alarm for you too.",
            MemberChatReplies.NotAQuestionReply("Dad"));
        Assert.Equal(
            "I didn't quite catch a question there. Ask me how their sleep, activity or heart rate have been, what's behind an alert, or to pull up the journal — and I can switch alerts on or off or set an alarm for you too.",
            MemberChatReplies.NotAQuestionReply(null));
    }
}
