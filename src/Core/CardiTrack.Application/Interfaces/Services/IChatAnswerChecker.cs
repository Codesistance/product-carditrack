using CardiTrack.Application.DTOs.Common;

namespace CardiTrack.Application.Interfaces.Services;

/// <summary>
/// Reads a finished member-chat reply against the question and the conversation it answers, and
/// judges whether it answered what was asked — and, when it did not, whether the data could have.
/// </summary>
public interface IChatAnswerChecker
{
    /// <param name="question">The caregiver's message, flattened and name-redacted.</param>
    /// <param name="history">The conversation so far, name-redacted, or null on a first message.</param>
    /// <param name="reply">The reply as the caregiver would read it, name-redacted.</param>
    Task<AiGenerationResult<ChatAnswerAssessment>> CheckAsync(
        string question, string? history, string reply, CancellationToken ct = default);
}
