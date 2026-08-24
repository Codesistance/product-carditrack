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

    /// <summary>
    /// The reply's supporting chart series as JSON, encrypted at rest exactly like
    /// <see cref="Content"/> — daily steps, heart rate and sleep are health data wherever they
    /// are written. Null on a caregiver turn, and on a reply that had nothing to chart.
    /// </summary>
    /// <remarks>
    /// Persisted rather than recomputed because a reply's charts describe the data as it was
    /// read when the answer was written; re-deriving them later would need the question's query
    /// plan, which is not kept, and would silently redraw an old answer against newer readings.
    /// Without this column any reload — pull-to-refresh, reopening the sheet, relaunching the
    /// app — rebuilt the thread from history and dropped every chart, which is what a caregiver
    /// saw as their graphs disappearing. The readings here are the same ones the reply text
    /// already spells out, so this adds no category of data to the turn it belongs to.
    /// </remarks>
    public string? Charts { get; set; }

    /// <summary>
    /// Which workflow produced this turn — set on the assistant turn, null on the caregiver's own
    /// and on every turn written before workflows were stamped.
    /// </summary>
    /// <remarks>
    /// Recoverable from <see cref="MemberChatTurnUsage"/> rows only for some of them: a status
    /// answer and an advise answer make exactly the same single call, as do a casual steer and an
    /// off-topic one. Storing it is what makes "which rung do caregivers actually stand on?"
    /// answerable — and that distribution is what decides whether the heaviest workflows are worth
    /// building at all (docs/technical/member_chat_routing.md §10).
    /// </remarks>
    public MemberChatWorkflow? Workflow { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}
