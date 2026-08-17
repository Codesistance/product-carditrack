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

    private readonly Guid _memberId = Guid.NewGuid();

    private QuestionnairesPageResponse EmptyPage() => new()
    {
        HasAny = false,
        Answered = new PagedResult<QuestionnaireResponse> { Items = [] },
    };

    [Fact]
    public async Task GetForMember_RefusesASignedOutCaller()
    {
        var result = await CreateSut(authenticated: false)
            .GetForMember(_memberId, search: null, page: 1, pageSize: 20, CancellationToken.None);

        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, objectResult.StatusCode);
        await _questionnaires.DidNotReceive().GetForMemberAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>A page or page size of zero — the default for an unset query parameter — is a
    /// caller's first request, not a malformed one, so it is corrected rather than rejected.</summary>
    [Theory]
    [InlineData(0, 0, 1, 20)]
    [InlineData(0, 999, 1, 50)]
    [InlineData(-3, 5, 1, 5)]
    public async Task GetForMember_ClampsPageAndPageSize(
        int requestedPage, int requestedPageSize, int expectedPage, int expectedPageSize)
    {
        _questionnaires.GetForMemberAsync(
                _userId, _memberId, Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(EmptyPage());

        await CreateSut().GetForMember(
            _memberId, search: null, page: requestedPage, pageSize: requestedPageSize, CancellationToken.None);

        await _questionnaires.Received(1).GetForMemberAsync(
            _userId, _memberId, null, expectedPage, expectedPageSize, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetForMember_PassesSearchThrough()
    {
        _questionnaires.GetForMemberAsync(
                _userId, _memberId, "bedrooms", Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(EmptyPage());

        var result = await CreateSut().GetForMember(
            _memberId, search: "bedrooms", page: 1, pageSize: 20, CancellationToken.None);

        Assert.IsAssignableFrom<ObjectResult>(result.Result);
        await _questionnaires.Received(1).GetForMemberAsync(
            _userId, _memberId, "bedrooms", 1, 20, Arg.Any<CancellationToken>());
    }

    // ---- Retiring a question whose day has ended ----

    [Fact]
    public async Task Expire_PassesThroughAsTheSignedInCaller()
    {
        _questionnaires.ExpireAsync(_userId, _questionnaireId, Arg.Any<CancellationToken>())
            .Returns(new QuestionnaireResponse
            {
                Id = _questionnaireId,
                QuestionText = "Did he feel tired at all today?",
                Status = "expired",
            });

        var result = await CreateSut().Expire(_questionnaireId, CancellationToken.None);

        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status200OK, objectResult.StatusCode);
        await _questionnaires.Received(1).ExpireAsync(
            _userId, _questionnaireId, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// An app can hold a question about a member it has since lost access to. The service reports
    /// not-found either way, and the endpoint must surface that rather than a 500.
    /// </summary>
    [Fact]
    public async Task Expire_ReportsNotFound_ForAQuestionThisCallerCannotReach()
    {
        _questionnaires.ExpireAsync(_userId, _questionnaireId, Arg.Any<CancellationToken>())
            .Returns<QuestionnaireResponse>(_ => throw new KeyNotFoundException("nope"));

        var result = await CreateSut().Expire(_questionnaireId, CancellationToken.None);

        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status404NotFound, objectResult.StatusCode);
    }

    [Fact]
    public async Task Expire_RefusesASignedOutCaller()
    {
        var result = await CreateSut(authenticated: false)
            .Expire(_questionnaireId, CancellationToken.None);

        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, objectResult.StatusCode);
        await _questionnaires.DidNotReceive().ExpireAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }
}
