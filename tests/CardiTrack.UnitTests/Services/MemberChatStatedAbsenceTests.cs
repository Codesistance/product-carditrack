using CardiTrack.Application.Services;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// The code-written sentence a reply gains when the answer check finds the question asked for
/// something the app does not hold (<see cref="MemberChatReplies.WithStatedAbsence"/>).
/// </summary>
public class MemberChatStatedAbsenceTests
{
    [Fact]
    public void ItIsAddedAtTheEnd_OfAPlainReply()
    {
        var reply = MemberChatReplies.WithStatedAbsence("Steps were about average this week.");

        Assert.Equal($"Steps were about average this week.\n\n{MemberChatReplies.StatedAbsenceSentence}", reply);
    }

    [Fact]
    public void ItGoesBeforeTheReferences_SoTheCitationsStayLast()
    {
        var reply = MemberChatReplies.WithStatedAbsence(
            "Sleep ran a little short.\n\nReferences: NHS sleep guidance.");

        Assert.Equal(
            $"Sleep ran a little short.\n\n{MemberChatReplies.StatedAbsenceSentence}\n\nReferences: NHS sleep guidance.",
            reply);
    }

    [Fact]
    public void ItIsNeverAddedTwice()
    {
        var once = MemberChatReplies.WithStatedAbsence("Steps were about average this week.");

        Assert.Equal(once, MemberChatReplies.WithStatedAbsence(once));
    }

    [Fact]
    public void ALongReply_MakesRoomForTheSentence_InsteadOfLosingIt()
    {
        const int cap = 300;
        var reply = MemberChatReplies.WithStatedAbsence(
            new string('a', 290) + "\n\nReferences: NHS sleep guidance.", cap);

        Assert.True(reply.Length <= cap, $"{reply.Length} > {cap}");
        Assert.Contains(MemberChatReplies.StatedAbsenceSentence, reply, StringComparison.Ordinal);
        Assert.EndsWith("\n\nReferences: NHS sleep guidance.", reply, StringComparison.Ordinal);
        Assert.Contains("…\n\n", reply, StringComparison.Ordinal);
    }
}
