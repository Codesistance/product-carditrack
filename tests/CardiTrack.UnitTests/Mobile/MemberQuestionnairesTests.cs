using CardiTrack.Mobile.Core.Questionnaires;

namespace CardiTrack.UnitTests.Mobile;

/// <summary>
/// Rules about a questionnaire answer that stay true regardless of where it renders. Picking out
/// the pending question and sorting/paging the answered ones moved server-side (see
/// <c>QuestionnaireServiceTests</c>) once the questions screen started fetching pages instead of
/// the whole history — only <see cref="MemberQuestionnaires.IsAnswerable"/> is still a client-side
/// rule.
/// </summary>
public class MemberQuestionnairesTests
{
    /// <summary>
    /// Blank is not an answer — there is a skip action for having nothing to say, and whitespace
    /// would reach the model as an answered question with nothing in it.
    /// </summary>
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("\n\t", false)]
    [InlineData("She moved bedrooms.", true)]
    public void IsAnswerable_RejectsWhitespace(string? answer, bool expected)
    {
        Assert.Equal(expected, MemberQuestionnaires.IsAnswerable(answer));
    }
}
