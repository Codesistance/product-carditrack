using CardiTrack.API.Validators;
using CardiTrack.Application.DTOs.Requests;

namespace CardiTrack.IntegrationTests.Validators;

/// <summary>
/// What the ask endpoint accepts. An empty question would still spend a MedGemma call and
/// put a blank under the caregiver-question heading; the dismiss-equivalent here is simply
/// not sending.
/// </summary>
public class AskMemberQuestionValidatorTests
{
    private readonly AskMemberQuestionValidator _sut = new();

    private bool IsValid(string? question) =>
        _sut.Validate(new AskMemberQuestionRequest { Question = question! }).IsValid;

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void Rejects_AQuestionWithNothingInIt(string? question)
    {
        Assert.False(IsValid(question));
    }

    [Fact]
    public void Accepts_AnOrdinaryQuestion()
    {
        Assert.True(IsValid("How did she sleep last night?"));
    }

    [Fact]
    public void Rejects_AQuestionPastTheCap()
    {
        Assert.True(IsValid(new string('a', AskMemberQuestionValidator.MaxQuestionLength)));
        Assert.False(IsValid(new string('a', AskMemberQuestionValidator.MaxQuestionLength + 1)));
    }
}
