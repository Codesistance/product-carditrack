namespace CardiTrack.Application.DTOs.Responses;

/// <summary>
/// One step of a member-chat send, reported while it runs so the app can say what is actually
/// happening instead of cycling generic waiting lines. <see cref="Step"/> is a stable key a
/// client may branch on; <see cref="Text"/> is the caregiver-facing line, written here in code —
/// never model output — so the copy can change without an app release.
/// </summary>
public sealed class MemberChatStep
{
    public required string Step { get; init; }
    public required string Text { get; init; }

    /// <summary>Where this step falls in the send, counting from 1 — set as the step is reported,
    /// never on the static definitions below, since the same step sits at a different place on
    /// different paths. Null on a step that was not numbered.</summary>
    public int? Index { get; init; }

    /// <summary>How many steps this send is expected to take, once that is known. Null on the
    /// first step: it is reported before the route is chosen, and the route decides the count.
    /// Grows by one when the inference rung takes a second look at the readings.</summary>
    public int? Total { get; init; }

    /// <summary>This step, numbered for where it fell in one send.</summary>
    public MemberChatStep At(int index, int? total) => new()
    {
        Step = Step,
        Text = Text,
        Index = index,
        Total = total,
    };

    /// <summary>The message passed the pre-check and is being routed to the workflow that
    /// answers it. The first step any send reports, and never reported before the pre-check has
    /// finished: a refused message must still end as a plain 400, not a stream.</summary>
    public static readonly MemberChatStep Understanding = new()
    {
        Step = "understanding",
        Text = "Working out what you're asking…",
    };

    /// <summary>Choosing which readings answer the question (the data-query plan).</summary>
    public static readonly MemberChatStep Planning = new()
    {
        Step = "planning",
        Text = "Choosing which readings to look at…",
    };

    /// <summary>The clinical read — the longest step by far.</summary>
    public static readonly MemberChatStep Reading = new()
    {
        Step = "reading",
        Text = "Looking closely at the readings — this is the longest part…",
    };

    /// <summary>A second clinical read, when the first disagreed with the dashboard.</summary>
    public static readonly MemberChatStep Rereading = new()
    {
        Step = "rereading",
        Text = "Taking a second look at the readings…",
    };

    /// <summary>Turning the clinical read into caregiver-plain language.</summary>
    public static readonly MemberChatStep Writing = new()
    {
        Step = "writing",
        Text = "Putting it into plain words…",
    };

    /// <summary>The answer check found the first reply missed the question; the workflow is
    /// running once more with the gap named. Follows a draft the caregiver can already read.</summary>
    public static readonly MemberChatStep Retrying = new()
    {
        Step = "retrying",
        Text = "Taking another look to answer exactly what you asked…",
    };

    /// <summary>Checking the reply against the question (the answer check).</summary>
    public static readonly MemberChatStep Checking = new()
    {
        Step = "checking",
        Text = "Checking the answer covers your question…",
    };
}
