using CardiTrack.Application.DTOs.Common;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Reports;

/// <summary>
/// One caregiver chat conversation, decrypted and ready to render — what a transcript export
/// covers.
/// </summary>
/// <remarks>
/// <para>
/// A transcript export is a different document from the health-data export beside it in this
/// folder: it carries what was asked and answered rather than the member's readings, so it rides
/// on <see cref="ReportDataSet.Transcript"/> instead of adding a section to
/// <see cref="ReportSections"/>. The member banner and the date range still come from the dataset
/// — the conversation is about a named person, and a page of it that does not say who would be
/// unfileable.
/// </para>
/// <para>
/// The turns arrive here already decrypted (see <c>IChatTranscriptSource</c>), so an instance of
/// this record is plaintext health data for as long as it is held. It is built inside one
/// generation and never cached.
/// </para>
/// </remarks>
public record ChatTranscript(
    Guid SessionId,
    Guid CardiMemberId,
    string? Theme,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset LastTurnAtUtc,
    IReadOnlyList<ChatTranscriptTurn> Turns)
{
    /// <summary>Caregiver questions, not total turns — the size a caregiver reads a conversation
    /// by, and the same count the history list shows.</summary>
    public int QuestionCount => Turns.Count(t => t.Role == ChatTurnRole.User);

    /// <summary>What the history list calls this conversation: its generated theme, or its
    /// opening question while the theming pass has not visited it.</summary>
    public string Label =>
        Theme is { Length: > 0 } theme
            ? theme
            : Turns.FirstOrDefault(t => t.Role == ChatTurnRole.User)?.Content is { Length: > 0 } first
                ? first
                : "Conversation";
}

/// <param name="Charts">
/// The supporting series the reply was written against, exactly as the app drew them — persisted
/// with the turn (<c>MemberChatTurn.Charts</c>) rather than recomputed, so an exported answer is
/// never redrawn against readings that arrived after it was written. Empty on a caregiver turn
/// and on a reply that had nothing to chart.
/// </param>
public record ChatTranscriptTurn(
    ChatTurnRole Role,
    string Content,
    DateTimeOffset CreatedAtUtc,
    IReadOnlyList<ChartSeries> Charts);
