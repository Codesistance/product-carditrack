using CardiTrack.Mobile.Core.Chat;

namespace CardiTrack.UnitTests.Mobile;

/// <summary>
/// The AI chat notice (EU AI Act Art. 50(1)) is remembered per caregiver, and says what the
/// obligation needs said: that the answers come from an AI system, not a clinician.
/// </summary>
public class AiChatNoticeTests
{
    [Fact]
    public void IsSeen_OnlyForTheCaregiverWhoSawIt()
    {
        var stored = AiChatNotice.SeenValueFor("ada@example.com");

        Assert.True(AiChatNotice.IsSeen(stored, "ada@example.com"));
        Assert.True(AiChatNotice.IsSeen(stored, "  ADA@example.com "));
        Assert.False(AiChatNotice.IsSeen(stored, "grace@example.com"));
    }

    [Fact]
    public void IsSeen_FalseWhenNothingStoredOrNoIdentity()
    {
        Assert.False(AiChatNotice.IsSeen(null, "ada@example.com"));
        Assert.False(AiChatNotice.IsSeen(string.Empty, "ada@example.com"));
        Assert.Null(AiChatNotice.SeenValueFor(null));
        Assert.False(AiChatNotice.IsSeen(AiChatNotice.SeenValueFor("ada@example.com"), null));
    }

    [Fact]
    public void Message_SaysTheAnswersComeFromAnAiAssistant_NotAClinician()
    {
        Assert.Contains("AI assistant", AiChatNotice.Message, StringComparison.Ordinal);
        Assert.Contains("not a clinician", AiChatNotice.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Message_StaysUnderThePopupTruncationLimit()
    {
        // AppPopupPage cuts messages at 600 characters; the notice must never be cut short.
        Assert.True(AiChatNotice.Message.Length <= 600, $"{AiChatNotice.Message.Length} chars");
    }
}
