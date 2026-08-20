using CardiTrack.Domain.Common;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Domain.Entities;

/// <summary>One message in a <see cref="MemberChatSession"/> — the caregiver's question, or the
/// rewritten answer.</summary>
/// <remarks>Deliberately not <c>ISoftDeletable</c> — see <see cref="MemberChatSession"/>.</remarks>
public class MemberChatTurn : BaseEntity
{
    public Guid SessionId { get; set; }

    public ChatTurnRole Role { get; set; }

    /// <summary>
    /// Full text, encrypted at rest — see <c>MemberChatService</c> for where the encryption happens
    /// (the same service-layer pattern as <see cref="MemberQuestionnaire.QuestionText"/>, not an EF
    /// value converter). Unlike the one-shot ask endpoint's response, this is not capped or
    /// discarded: it is read back as conversation history for later turns in the same session, so it
    /// carries the same untrusted-context framing on the way back in as it did on the way out — see
    /// <c>MemberContextComposer</c>.
    /// </summary>
    public string Content { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }
}
