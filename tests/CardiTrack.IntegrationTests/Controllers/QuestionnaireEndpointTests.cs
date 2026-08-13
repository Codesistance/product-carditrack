using CardiTrack.API.Controllers;
using CardiTrack.API.Infrastructure.UserContext;
using CardiTrack.API.Validators;
using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Interfaces.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace CardiTrack.IntegrationTests.Controllers;

/// <summary>
/// The answer endpoint, exercised through the controller rather than through the validator alone.
/// </summary>
/// <remarks>
/// The distinction is the whole point of this class. A validator can be perfectly correct and still
/// never run: this one was written, unit-tested against directly, and neither registered nor
/// invoked — so every blank and over-long answer reached the service and was stored. Testing the
/// validator in isolation proved only that the rules were right, which was never the thing in
/// doubt. These tests go through the action, so an unwired validator fails them.
/// </remarks>
public class QuestionnaireEndpointTests
{
    private readonly IQuestionnaireService _questionnaires = Substitute.For<IQuestionnaireService>();
    private readonly IUserContext _userContext = Substitute.For<IUserContext>();
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _questionnaireId = Guid.NewGuid();

    private QuestionnairesController CreateSut(bool authenticated = true)
    {
        _userContext.IsAuthenticated.Returns(authenticated);
        _userContext.UserId.Returns(authenticated ? _userId : Guid.Empty);

        return new QuestionnairesController(
            _userContext,
            Substitute.For<ILogger<QuestionnairesController>>(),
            _questionnaires,
            new AnswerQuestionnaireValidator());
    }

    private static AnswerQuestionnaireRequest Request(string answer) => new() { AnswerText = answer };

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public async Task Answer_RejectsABlankAnswer_WithoutReachingTheService(string answer)
    {
        var result = await CreateSut().Answer(_questionnaireId, Request(answer), CancellationToken.None);

        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, objectResult.StatusCode);
        await _questionnaires.DidNotReceive().AnswerAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Answer_RejectsAnAnswerPastTheStorageCap_WithoutReachingTheService()
    {
        var result = await CreateSut().Answer(
            _questionnaireId, Request(new string('a', 2_001)), CancellationToken.None);

        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, objectResult.StatusCode);
        await _questionnaires.DidNotReceive().AnswerAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Answer_PassesAnOrdinaryAnswerThrough()
    {
        _questionnaires.AnswerAsync(_userId, _questionnaireId, "She moved bedrooms.", Arg.Any<CancellationToken>())
            .Returns(new QuestionnaireResponse
            {
                Id = _questionnaireId,
                QuestionText = "Has anything changed at home recently?",
                AnswerText = "She moved bedrooms.",
                Status = "answered",
            });

        var result = await CreateSut().Answer(
            _questionnaireId, Request("She moved bedrooms."), CancellationToken.None);

        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status200OK, objectResult.StatusCode);
        await _questionnaires.Received(1).AnswerAsync(
            _userId, _questionnaireId, "She moved bedrooms.", Arg.Any<CancellationToken>());
    }

    /// <summary>Signed out is refused before the body is looked at, like every other write here.</summary>
    [Fact]
    public async Task Answer_RefusesASignedOutCaller()
    {
        var result = await CreateSut(authenticated: false)
            .Answer(_questionnaireId, Request("She moved bedrooms."), CancellationToken.None);

        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, objectResult.StatusCode);
        await _questionnaires.DidNotReceive().AnswerAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
