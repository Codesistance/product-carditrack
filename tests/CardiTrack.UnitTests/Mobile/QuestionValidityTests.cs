using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Core.Questionnaires;

namespace CardiTrack.UnitTests.Mobile;

/// <summary>
/// Whether a question the app is holding is still worth putting in front of a caregiver. The
/// failure it answers: a card held on screen across midnight, or a page served from the offline
/// cache after a night with no signal, asking "did he feel tired at all today?" on a different
/// today.
/// </summary>
public class QuestionValidityTests
{
    private static readonly DateTime Now = new(2026, 8, 17, 7, 15, 0, DateTimeKind.Utc);

    private static QuestionnaireResponse Question(DateTime? askableUntilUtc) => new()
    {
        Id = Guid.NewGuid(),
        QuestionText = "Did he feel tired at all today?",
        Status = "pending",
        AskableUntilUtc = askableUntilUtc,
    };

    /// <summary>
    /// A standing-fact question, and every row written before questions carried a validity at all.
    /// Null is "never lapses", the same reading the server gives it.
    /// </summary>
    [Fact]
    public void AQuestionWithNoDeadline_NeverLapses()
    {
        Assert.False(QuestionValidity.HasLapsed(Question(askableUntilUtc: null), Now));
    }

    [Fact]
    public void AQuestionInsideItsDay_IsStillWorthAsking()
    {
        Assert.False(QuestionValidity.HasLapsed(Question(Now.AddHours(4)), Now));
    }

    [Fact]
    public void AQuestionPastItsDay_HasLapsed()
    {
        Assert.True(QuestionValidity.HasLapsed(Question(Now.AddHours(-4)), Now));
    }

    /// <summary>
    /// Phone clocks drift and can be set by hand. A device running fast must not retire a question
    /// the rest of the family still has in front of them, so the boundary is deliberately generous
    /// in the direction of keeping a live card.
    /// </summary>
    [Theory]
    [InlineData(-1, false)]   // one minute past: inside the allowance, still drawn
    [InlineData(-9, false)]
    [InlineData(-10, true)]   // the allowance itself
    [InlineData(-30, true)]
    public void TheDeadline_IsGivenAMarginForAWrongDeviceClock(int minutesPastDeadline, bool expected)
    {
        var question = Question(Now.AddMinutes(minutesPastDeadline));

        Assert.Equal(expected, QuestionValidity.HasLapsed(question, Now));
    }

    [Fact]
    public void StillWorthAsking_PassesNullStraightThrough()
    {
        Assert.Null(QuestionValidity.StillWorthAsking(null, Now));
    }

    [Fact]
    public void StillWorthAsking_DropsTheLapsedOneAndKeepsTheLive()
    {
        Assert.Null(QuestionValidity.StillWorthAsking(Question(Now.AddHours(-4)), Now));
        Assert.NotNull(QuestionValidity.StillWorthAsking(Question(Now.AddHours(4)), Now));
    }
}
