using CardiTrack.API.Validators;
using CardiTrack.Application.DTOs.Requests;

namespace CardiTrack.IntegrationTests.Validators;

/// <summary>
/// What the answer endpoint accepts. The blank cases matter more than they look: an answer stored
/// as whitespace is a question the apps render as answered and later prompts read as context, with
/// nothing in it either way — and there is a dismiss action for having nothing to say.
/// </summary>
public class AnswerQuestionnaireValidatorTests
{
    private readonly AnswerQuestionnaireValidator _sut = new();

    private bool IsValid(string? answer) =>
        _sut.Validate(new AnswerQuestionnaireRequest { AnswerText = answer! }).IsValid;

    /// <summary>
    /// Whitespace included, and pinned rather than assumed: FluentValidation's <c>NotEmpty</c>
    /// treats a whitespace-only string as empty, but that is a library behaviour this endpoint's
    /// correctness now rests on, so it is asserted here rather than trusted to stay true.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void Rejects_AnAnswerWithNothingInIt(string? answer)
    {
        Assert.False(IsValid(answer));
    }

    [Fact]
    public void Accepts_AnOrdinaryAnswer()
    {
        Assert.True(IsValid("She moved to the downstairs bedroom last week."));
    }

    [Fact]
    public void Rejects_AnAnswerPastTheStorageCap()
    {
        Assert.True(IsValid(new string('a', 2_000)));
        Assert.False(IsValid(new string('a', 2_001)));
    }
}
