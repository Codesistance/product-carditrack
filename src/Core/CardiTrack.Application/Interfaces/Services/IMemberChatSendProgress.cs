using CardiTrack.Application.DTOs.Responses;

namespace CardiTrack.Application.Interfaces.Services;

/// <summary>
/// What a streamed member-chat send reports while it runs — see
/// <see cref="IMemberChatService.SendMessageAsync(Guid, Guid, string, IMemberChatSendProgress?, CancellationToken)"/>.
/// Both calls are synchronous and must not block: the pipeline never waits on the reader.
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
}
