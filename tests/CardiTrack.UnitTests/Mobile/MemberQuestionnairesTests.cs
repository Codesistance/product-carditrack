using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Core.Questionnaires;

namespace CardiTrack.UnitTests.Mobile;

/// <summary>
/// How the questions surface sorts what the API hands it: which one is waiting, which ones are
/// worth looking back on, and what counts as an answer.
/// </summary>
public class MemberQuestionnairesTests
{
    private static QuestionnaireResponse Questionnaire(
        string status, DateTime? answeredAtUtc = null, string? question = null) => new()
        {
            Id = Guid.NewGuid(),
            CardiMemberId = Guid.NewGuid(),
            QuestionText = question ?? "Has anything changed at home recently?",
            AnswerText = answeredAtUtc is null ? null : "She moved bedrooms.",
            Status = status,
            GeneratedAtUtc = new DateTime(2026, 8, 1, 9, 0, 0, DateTimeKind.Utc),
            AnsweredAtUtc = answeredAtUtc,
        };

    [Fact]
    public void FirstPending_FindsTheQuestionStillWaiting()
    {
        var pending = Questionnaire("pending", question: "Waiting?");

        var found = MemberQuestionnaires.FirstPending(
            [Questionnaire("answered", DateTime.UtcNow), pending, Questionnaire("dismissed")]);

        Assert.Same(pending, found);
    }

    [Fact]
    public void FirstPending_IsNullWhenNothingIsWaiting()
    {
        Assert.Null(MemberQuestionnaires.FirstPending(
            [Questionnaire("answered", DateTime.UtcNow), Questionnaire("dismissed")]));
        Assert.Null(MemberQuestionnaires.FirstPending([]));
        Assert.Null(MemberQuestionnaires.FirstPending(null));
    }

    /// <summary>
    /// The API sends lowercase status strings, but the client must not be the reason a screen goes
    /// blank if that ever changes casing.
    /// </summary>
    [Theory]
    [InlineData("Pending")]
    [InlineData("PENDING")]
    public void FirstPending_DoesNotDependOnCasing(string status)
    {
        Assert.NotNull(MemberQuestionnaires.FirstPending([Questionnaire(status)]));
    }

    /// <summary>
    /// The pipeline will not ask a second thing while the first is unanswered, but a client that
    /// crashed on a server it disagreed with would be a worse way to discover that.
    /// </summary>
    [Fact]
    public void FirstPending_TakesTheFirst_WhenTheServerSendsMoreThanOne()
    {
        var first = Questionnaire("pending", question: "First?");

        var found = MemberQuestionnaires.FirstPending([first, Questionnaire("pending", question: "Second?")]);

        Assert.Same(first, found);
    }

    [Fact]
    public void AnsweredNewestFirst_KeepsOnlyAnsweredOnes_MostRecentlyAnsweredFirst()
    {
        var older = Questionnaire("answered", new DateTime(2026, 8, 2, 9, 0, 0, DateTimeKind.Utc), "Older?");
        var newer = Questionnaire("answered", new DateTime(2026, 8, 9, 9, 0, 0, DateTimeKind.Utc), "Newer?");

        var answered = MemberQuestionnaires.AnsweredNewestFirst(
            [older, Questionnaire("pending"), newer, Questionnaire("dismissed")]);

        Assert.Equal(["Newer?", "Older?"], answered.Select(q => q.QuestionText));
    }

    [Fact]
    public void AnsweredNewestFirst_IsEmptyRatherThanNull()
    {
        Assert.Empty(MemberQuestionnaires.AnsweredNewestFirst(null));
        Assert.Empty(MemberQuestionnaires.AnsweredNewestFirst([Questionnaire("pending")]));
    }

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
