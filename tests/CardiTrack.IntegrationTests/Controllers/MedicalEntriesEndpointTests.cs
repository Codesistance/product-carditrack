using CardiTrack.API.Controllers;
using CardiTrack.API.Infrastructure.UserContext;
using CardiTrack.API.Validators;
using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Enums;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace CardiTrack.IntegrationTests.Controllers;

/// <summary>
/// The medical-information ledger's endpoints, through the controller so an unwired validator or a
/// missing exception mapping fails here — see <see cref="QuestionnaireEndpointTests"/> for why.
/// </summary>
public class MedicalEntriesEndpointTests
{
    private readonly IMedicalEntryService _entries = Substitute.For<IMedicalEntryService>();
    private readonly IUserContext _userContext = Substitute.For<IUserContext>();
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _memberId = Guid.NewGuid();
    private readonly Guid _entryId = Guid.NewGuid();

    private MedicalEntriesController CreateSut(bool authenticated = true)
    {
        _userContext.IsAuthenticated.Returns(authenticated);
        _userContext.UserId.Returns(authenticated ? _userId : Guid.Empty);

        return new MedicalEntriesController(
            _userContext,
            Substitute.For<ILogger<MedicalEntriesController>>(),
            _entries,
            new MedicalEntryValidator());
    }

    private static MedicalEntryRequest Request(string text, MedicalEntryKind kind = MedicalEntryKind.Allergy) =>
        new() { Kind = kind, Text = text };

    private static int StatusOf<T>(ActionResult<T> result) =>
        Assert.IsAssignableFrom<ObjectResult>(result.Result).StatusCode!.Value;

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Add_RejectsABlankLine_WithoutReachingTheService(string text)
    {
        var result = await CreateSut().Add(_memberId, Request(text), CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(result));
        await _entries.DidNotReceiveWithAnyArgs().AddAsync(default, default, default, default!, default);
    }

    [Fact]
    public async Task Add_RejectsALinePastTheCap_WithoutReachingTheService()
    {
        var result = await CreateSut().Add(
            _memberId, Request(new string('a', MedicalLedger.MaxEntryLength + 1)), CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(result));
        await _entries.DidNotReceiveWithAnyArgs().AddAsync(default, default, default, default!, default);
    }

    [Fact]
    public async Task Add_RejectsAKindThatDoesNotExist()
    {
        var result = await CreateSut().Add(
            _memberId, Request("Penicillin", (MedicalEntryKind)99), CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(result));
    }

    [Fact]
    public async Task Add_PassesAnOrdinaryLineThrough()
    {
        _entries.AddAsync(_userId, _memberId, MedicalEntryKind.Allergy, "Penicillin", Arg.Any<CancellationToken>())
            .Returns(new MedicalEntriesResponse());

        var result = await CreateSut().Add(_memberId, Request("Penicillin"), CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, StatusOf(result));
    }

    /// <summary>A list that has outgrown what the note can hold is the caregiver's to fix: 400, with the reason.</summary>
    [Fact]
    public async Task Add_WhenTheListIsFull_Is400()
    {
        _entries.AddAsync(_userId, _memberId, Arg.Any<MedicalEntryKind>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("That's more than the medical information can hold."));

        var result = await CreateSut().Add(_memberId, Request("Penicillin"), CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(result));
    }

    [Fact]
    public async Task Revise_ValidatesTheBody()
    {
        var result = await CreateSut().Revise(_memberId, _entryId, Request(" "), CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(result));
        await _entries.DidNotReceiveWithAnyArgs().ReviseAsync(default, default, default, default, default!, default);
    }

    /// <summary>A member or line the caller cannot reach is 404, never 403 — see <see cref="ICardiMemberAccessService"/>.</summary>
    [Fact]
    public async Task Remove_OfALineTheCallerCannotReach_Is404()
    {
        _entries.RemoveAsync(_userId, _memberId, _entryId, Arg.Any<CancellationToken>())
            .ThrowsAsync(new KeyNotFoundException("CardiMember not found"));

        var result = await CreateSut().Remove(_memberId, _entryId, CancellationToken.None);

        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(result));
    }

    [Fact]
    public async Task EveryAction_RefusesASignedOutCaller()
    {
        var sut = CreateSut(authenticated: false);

        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(await sut.Get(_memberId, CancellationToken.None)));
        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(await sut.Add(_memberId, Request("x"), CancellationToken.None)));
        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(await sut.Revise(_memberId, _entryId, Request("x"), CancellationToken.None)));
        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(await sut.Remove(_memberId, _entryId, CancellationToken.None)));
        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(await sut.Confirm(_memberId, _entryId, CancellationToken.None)));
        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(await sut.Erase(_memberId, _entryId, CancellationToken.None)));
        Assert.Empty(_entries.ReceivedCalls());
    }
}
