namespace CardiTrack.Mobile.Core.Questionnaires;

/// <summary>
/// Rules about a questionnaire answer that stay true regardless of where it renders.
/// </summary>
/// <remarks>
/// Out of the page's code-behind so it can be tested without a UI, in the same spirit as
/// <c>DeviceDatasets</c> and <c>TrendScale</c>. Picking out the pending question and paging/sorting
/// the answered ones used to live here too, back when a page fetched every questionnaire and sorted
/// them client-side; the API now does both server-side (see
/// <c>QuestionnaireService.GetForMemberAsync</c>), so this is only what is still true regardless of
/// where an answer came from.
/// </remarks>
public static class MemberQuestionnaires
{
    /// <summary>
    /// Whether this is worth sending. Blank is not an answer — there is a skip action for having
    /// nothing to say, and storing whitespace would put an answered question with nothing in it in
    /// front of the model.
    /// </summary>
    public static bool IsAnswerable(string? answer) => !string.IsNullOrWhiteSpace(answer);

    /// <summary>
    /// A question about right now ("did they have visitors yesterday?") rather than a standing
    /// fact. Unknown or missing scope reads as momentary — that is the model's default, and the
    /// safer caption if a row arrives without one.
    /// </summary>
    public static bool IsForTheMoment(string? scope) =>
        !string.Equals(scope, "permanent", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The footnote under the question. Momentary ones say so, so a caregiver does not think we
    /// will keep "yesterday" on this list; standing ones say we will keep them here.
    /// </summary>
    public static string Softener(string? scope, bool isAnswered) =>
        IsForTheMoment(scope)
            ? isAnswered
                ? "This was just for the moment. You can still change or remove it."
                : "Just for the moment — answering is optional."
            : isAnswered
                ? "We'll keep this here. You can change or remove it whenever you like."
                : "We'll keep this here. Answering is optional.";
}
