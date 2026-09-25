using CardiTrack.Application.DTOs.Responses;

namespace CardiTrack.Application.Interfaces.Services;

/// <summary>
/// What a streamed member-chat send reports while it runs — see
/// <see cref="IMemberChatService.SendMessageAsync(Guid, Guid, string, IMemberChatSendProgress?, CancellationToken)"/>.
/// Every call is synchronous and must not block: the pipeline never waits on the reader.
/// </summary>
public interface IMemberChatSendProgress
{
    /// <summary>A stage of the pipeline is starting.</summary>
    void Step(MemberChatStep step);

    /// <summary>
    /// The first reply, as the caregiver would see it, before the answer check has read it —
    /// shown at once, and replaced if the check's remedy changes it. Not yet saved: the turn is
    /// saved with whichever reply is final.
    /// </summary>
    void Draft(MemberChatMessageResponse draft);

    /// <summary>
    /// Short lines about what is being checked for this question, for the app to rotate under a
    /// step that is taking a while. Reported at most once, only on a path that reads the readings,
    /// and only if they are ready before the send settles; never when their generation fails.
    /// </summary>
    void WaitingLines(IReadOnlyList<string> lines);
}
