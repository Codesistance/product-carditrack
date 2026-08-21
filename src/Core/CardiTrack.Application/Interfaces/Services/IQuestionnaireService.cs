using CardiTrack.Application.DTOs.Responses;

namespace CardiTrack.Application.Interfaces.Services;

/// <summary>
/// The family's side of the questionnaire loop: reading what has been asked, answering it, changing
/// an answer, and removing one.
/// </summary>
/// <remarks>
/// Generation is not here — questions are written by the digest pass in the AI pipeline, which is
/// where LLM work belongs. This is only what a caregiver does with one afterwards.
/// </remarks>
public interface IQuestionnaireService
{
    /// <summary>
    /// The pending question (if any) and a page of the answered history, newest first, optionally
    /// filtered to those whose question or answer text contains <paramref name="search"/>.
    /// </summary>
    Task<QuestionnairesPageResponse> GetForMemberAsync(
        Guid requestingUserId,
        Guid cardiMemberId,
        string? search,
        int page,
        int pageSize,
        CancellationToken ct = default);

    /// <summary>
    /// The pending question alone, or null when there isn't one — what
    /// <see cref="DashboardService"/> shows on the CardiMember card, where fetching (and decrypting)
    /// the whole history via <see cref="GetForMemberAsync"/> just for this would be wasted work on
    /// a screen that refreshes every 30 seconds.
    /// </summary>
    Task<QuestionnaireResponse?> GetPendingAsync(
        Guid requestingUserId, Guid cardiMemberId, CancellationToken ct = default);

    /// <summary>
    /// Records an answer. The same call answers a pending question and replaces an existing answer —
    /// editing is not a different act, and a second endpoint would only be a second thing to keep
    /// authorised.
    /// </summary>
    Task<QuestionnaireResponse> AnswerAsync(
        Guid requestingUserId, Guid questionnaireId, string answerText, CancellationToken ct = default);

    /// <summary>
    /// Skips the question without answering it. The record survives, so the same ground is not
    /// covered again — see <see cref="DeleteAsync"/> for the destructive option.
    /// </summary>
    Task<QuestionnaireResponse> DismissAsync(
        Guid requestingUserId, Guid questionnaireId, CancellationToken ct = default);

    /// <summary>
    /// Retires a question that has outlived the moment it asked about, so it stops waiting on a
    /// family and stops blocking the next one — see
    /// <see cref="Domain.Entities.MemberQuestionnaire.AskableUntilUtc"/>.
    /// </summary>
    /// <remarks>
    /// Idempotent, and judged against the server's clock rather than the caller's claim: an app that
    /// asks to retire a question still inside its window is answered with the question unchanged.
    /// Not the same act as <see cref="DismissAsync"/> — nobody decided anything here, and no promise
    /// is made about never covering that ground again.
    /// </remarks>
    Task<QuestionnaireResponse> ExpireAsync(
        Guid requestingUserId, Guid questionnaireId, CancellationToken ct = default);

    /// <summary>
    /// Removes the question and its answer outright.
    /// </summary>
    /// <remarks>
    /// A real row delete, not a flag. The answer is something a family member wrote about a person
    /// who never signed up to this service, so "delete" has to mean gone (GDPR Art. 17) — the rest
    /// of the platform's soft-delete convention is the wrong instinct here.
    /// </remarks>
    Task DeleteAsync(Guid requestingUserId, Guid questionnaireId, CancellationToken ct = default);
}
