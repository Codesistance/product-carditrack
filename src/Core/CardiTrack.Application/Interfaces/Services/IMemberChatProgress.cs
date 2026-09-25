using CardiTrack.Application.DTOs.Responses;

namespace CardiTrack.Application.Interfaces.Services;

/// <summary>
/// A progress sink that can also take the question-specific waiting lines — the streaming
/// endpoint's. <see cref="IMemberChatService.SendMessageAsync(Guid, Guid, string, IProgress{MemberChatStep}?, CancellationToken)"/>
/// generates those lines only when the sink it is handed is one of these: the JSON endpoint and
/// the tests pass a plain <see cref="IProgress{T}"/> or nothing, and a send nobody is watching must
/// not pay for a model call whose output would go nowhere.
/// </summary>
public interface IMemberChatProgress : IProgress<MemberChatStep>
{
    /// <summary>
    /// Short lines about what is being checked for this question, for the app to rotate under a
    /// step that is taking a while. Reported at most once per send, and only if they are ready
    /// before the answer; never reported when generation fails.
    /// </summary>
    void ReportWaitingLines(IReadOnlyList<string> lines);
}
