using CardiTrack.Mobile.Core.Questionnaires;

namespace CardiTrack.UnitTests.Mobile;

/// <summary>
/// Rules about a questionnaire answer that stay true regardless of where it renders. Picking out
/// the pending question and sorting/paging the answered ones moved server-side (see
/// <c>QuestionnaireServiceTests</c>) once the questions screen started fetching pages instead of
/// the whole history — only the client-side rules about a single answer remain here.
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

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("timescoped", true)]
    [InlineData("permanent", false)]
    [InlineData("Permanent", false)]
    public void IsForTheMoment_TreatsAnythingButPermanentAsMomentary(string? scope, bool expected)
    {
        Assert.Equal(expected, MemberQuestionnaires.IsForTheMoment(scope));
    }

    [Theory]
    [InlineData("timescoped", false, "Just for the moment — answering is optional.")]
    [InlineData("timescoped", true, "This was just for the moment. You can still change or remove it.")]
    [InlineData("permanent", false, "We'll keep this here. Answering is optional.")]
    [InlineData("permanent", true, "We'll keep this here. You can change or remove it whenever you like.")]
    public void Softener_SaysWhetherTheAnswerStaysOnTheList(string scope, bool isAnswered, string expected)
    {
        Assert.Equal(expected, MemberQuestionnaires.Softener(scope, isAnswered));
    }
}
