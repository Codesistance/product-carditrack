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
}
