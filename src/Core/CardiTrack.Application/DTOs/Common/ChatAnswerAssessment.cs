using System.Text.Json;
using System.Text.Json.Serialization;

namespace CardiTrack.Application.DTOs.Common;

/// <summary>How much of the question a reply answered, as the answer check judged it.</summary>
public enum AnswerCompleteness
{
    /// <summary>Everything the question asked for.</summary>
    Full = 1,

    /// <summary>Some of it — an answer beside the point, or half of a two-part question.</summary>
    Partial = 2,

    /// <summary>None of it.</summary>
    None = 3,
}

/// <summary>Why a reply fell short, when it did.</summary>
public enum AnswerGapCause
{
    /// <summary>
    /// The data could answer it and the reply did not — the case a second attempt can fix.
    /// </summary>
    NotAddressed = 1,

    /// <summary>
    /// What was asked is not something the app holds ("when was he active" against daily
    /// totals). No retry can fix it; the honest reply says what is not on file.
    /// </summary>
    NotInData = 2,
}

/// <summary>What the send did about a reply the check found short.</summary>
public enum AnswerRemedy
{
    /// <summary>Nothing: the reply was full, the check failed, or no remedy applies to the
    /// workflow (a code-written status or advise reply cannot be asked again).</summary>
    None = 0,

    /// <summary><see cref="AnswerGapCause.NotInData"/>: a sentence written in code says the
    /// detail is not on file. No model call.</summary>
    StatedAbsence = 1,

    /// <summary><see cref="AnswerGapCause.NotAddressed"/>: the workflow ran once more with the
    /// gap named, and its reply replaced the first.</summary>
    Retried = 2,

    /// <summary>The retry was attempted and failed; the first reply stands.</summary>
    RetryFailed = 3,

    /// <summary>A retry was due but not run: the send was not streamed, so nobody was being
    /// shown the first answer while a second was worked on.</summary>
    RetrySkipped = 4,
}

/// <summary>
/// The answer check's reading of one reply: what the question was after, whether the reply gave
/// it, and why not when it did not. Internal — stored encrypted on the assistant turn and never
/// shown to the caregiver.
/// </summary>
/// <remarks>
/// <para>
/// The free-text fields are model output about a question concerning a named person, which makes
/// them the same class of content as the turn itself (DPIA R-A17) — encrypted at rest with it,
/// kept and erased with it, and never logged. Only <see cref="Completeness"/> and
/// <see cref="Cause"/> leave the row, as telemetry tags.
/// </para>
/// <para>
/// Acted on since 2026-09-25 — see <see cref="Remedy"/> and docs/technical/member_chat_routing.md,
/// "The answer check". The verdict stored is the first reply's; a retried reply is not checked
/// again, so the stored verdict says why the retry ran, not how it went.
/// </para>
/// </remarks>
public sealed record ChatAnswerAssessment
{
    private static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public required AnswerCompleteness Completeness { get; init; }

    /// <summary>Null when <see cref="Completeness"/> is <see cref="AnswerCompleteness.Full"/>.</summary>
    public AnswerGapCause? Cause { get; init; }

    /// <summary>What the question was after, in a line.</summary>
    public string? Intent { get; init; }

    /// <summary>What the reply left out, when it left something out.</summary>
    public string? Missing { get; init; }

    /// <summary>The check's reasoning, in a sentence or two.</summary>
    public string? Reasoning { get; init; }

    /// <summary>What the send did about the verdict. Absent from assessments stored before
    /// remedies existed, which read as <see cref="AnswerRemedy.None"/>.</summary>
    public AnswerRemedy Remedy { get; init; }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Null for anything unreadable — a stored assessment is a record, not a contract.</summary>
    public static ChatAnswerAssessment? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<ChatAnswerAssessment>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
