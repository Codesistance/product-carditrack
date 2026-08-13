using CardiTrack.Application.DTOs.Responses;

namespace CardiTrack.Mobile.Core.Questionnaires;

/// <summary>
/// Sorting the questions a member's page has been given into the one still waiting and the ones
/// already answered.
/// </summary>
/// <remarks>
/// Out of the page's code-behind so it can be tested without a UI, in the same spirit as
/// <c>DeviceDatasets</c> and <c>TrendScale</c>.
/// </remarks>
public static class MemberQuestionnaires
{
    private const string Pending = "pending";
    private const string Answered = "answered";

    /// <summary>
    /// The question waiting on an answer, or null when there is none.
    /// </summary>
    /// <remarks>
    /// Takes the first of several rather than asserting there is one. The pipeline will not ask a
    /// second thing while the first is unanswered, but a client that crashed on a server it
    /// disagreed with would be a worse way to find that out.
    /// </remarks>
    public static QuestionnaireResponse? FirstPending(IEnumerable<QuestionnaireResponse>? questionnaires) =>
        questionnaires?.FirstOrDefault(q => IsStatus(q, Pending));

    /// <summary>The answered ones, newest answer first — what the list page shows.</summary>
    public static IReadOnlyList<QuestionnaireResponse> AnsweredNewestFirst(
        IEnumerable<QuestionnaireResponse>? questionnaires) =>
        questionnaires?
            .Where(q => IsStatus(q, Answered))
            .OrderByDescending(q => q.AnsweredAtUtc ?? q.GeneratedAtUtc)
            .ToList()
        ?? [];

    /// <summary>
    /// Whether this is worth sending. Blank is not an answer — there is a skip action for having
    /// nothing to say, and storing whitespace would put an answered question with nothing in it in
    /// front of the model.
    /// </summary>
    public static bool IsAnswerable(string? answer) => !string.IsNullOrWhiteSpace(answer);

    private static bool IsStatus(QuestionnaireResponse questionnaire, string status) =>
        string.Equals(questionnaire.Status, status, StringComparison.OrdinalIgnoreCase);
}
