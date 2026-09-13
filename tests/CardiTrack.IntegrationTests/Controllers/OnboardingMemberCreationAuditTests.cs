using CardiTrack.API.Controllers;
using CardiTrack.API.Infrastructure.Auditing;
using CardiTrack.API.Infrastructure.UserContext;
using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Exceptions;
using CardiTrack.Application.Interfaces.Services;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace CardiTrack.IntegrationTests.Controllers;

/// <summary>
/// The onboarding create is audited, and a create has no member id in its route — the action
/// hands the id to <c>AuditLoggingMiddleware</c> through <c>HttpContext.Items</c>. That hand-off
/// has to happen on success and on the one failure where a member may nonetheless exist.
/// </summary>
public class OnboardingMemberCreationAuditTests
{
    private readonly ICardiMemberService _members = Substitute.For<ICardiMemberService>();
    private readonly IUserContext _userContext = Substitute.For<IUserContext>();
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _organizationId = Guid.NewGuid();
    private readonly DefaultHttpContext _httpContext = new();

    private OnboardingController CreateSut()
    {
        _userContext.IsAuthenticated.Returns(true);
        _userContext.UserId.Returns(_userId);
        _userContext.OrganizationId.Returns(_organizationId);

        var memberValidator = Substitute.For<IValidator<CreateCardiMemberRequest>>();
        memberValidator.ValidateAsync(Arg.Any<CreateCardiMemberRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ValidationResult());

        return new OnboardingController(
            _userContext,
            Substitute.For<ILogger<OnboardingController>>(),
            Substitute.For<IOrganizationService>(),
            Substitute.For<IUserService>(),
            _members,
            Substitute.For<IOnboardingService>(),
            Substitute.For<IValidator<CreateOrganizationRequest>>(),
            memberValidator)
        {
            ControllerContext = new ControllerContext { HttpContext = _httpContext }
        };
    }

    private static CreateCardiMemberRequest Request() => new() { Name = "Margaret Doe" };

    private object? HandedOverId => _httpContext.Items.TryGetValue(
        AuditHealthDataAccessAttribute.CardiMemberIdItemKey, out var id) ? id : null;

    [Fact]
    public async Task OnSuccess_HandsTheCreatedIdToTheAuditMiddleware()
    {
        var created = Guid.NewGuid();
        _members.CreateCardiMemberAsync(_organizationId, _userId, Arg.Any<CreateCardiMemberRequest>())
            .Returns(new CardiMemberResponse { Id = created, Name = "Margaret Doe" });

        var result = await CreateSut().CreateCardiMember(Request());

        Assert.Equal(StatusCodes.Status201Created, ((ObjectResult)result.Result!).StatusCode);
        Assert.Equal(created, HandedOverId);
    }

    [Fact]
    public async Task WhenTheCommitOutcomeIsUnknown_StillHandsTheIdOver_AndRethrows()
    {
        // The member may exist. The entry the middleware writes for this 500 must name it.
        var maybeCreated = Guid.NewGuid();
        _members.CreateCardiMemberAsync(_organizationId, _userId, Arg.Any<CreateCardiMemberRequest>())
            .Returns<CardiMemberResponse>(_ => throw new CardiMemberCreationOutcomeUnknownException(
                maybeCreated, new TimeoutException("commit ack lost")));

        await Assert.ThrowsAsync<CardiMemberCreationOutcomeUnknownException>(
            () => CreateSut().CreateCardiMember(Request()));

        Assert.Equal(maybeCreated, HandedOverId);
    }

    [Fact]
    public async Task WhenTheCreateFailsBeforeTheCommit_HandsNothingOver()
    {
        // Rolled back: no member exists, so there is no id to name and the failure is its own.
        _members.CreateCardiMemberAsync(_organizationId, _userId, Arg.Any<CreateCardiMemberRequest>())
            .Returns<CardiMemberResponse>(_ => throw new InvalidOperationException("link save failed"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateSut().CreateCardiMember(Request()));

        Assert.Null(HandedOverId);
    }
}
