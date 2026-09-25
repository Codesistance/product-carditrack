using CardiTrack.API.Controllers;
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
using NSubstitute.ExceptionExtensions;

namespace CardiTrack.IntegrationTests.Controllers;

/// <summary>
/// Adding a CardiMember past the plan's limit. The refusal used to reach the exception
/// middleware uncaught and come back as "Something went wrong on our end" — a reason the service
/// had written, reported as an outage.
/// </summary>
public class OnboardingCardiMemberLimitTests
{
    private readonly ICardiMemberService _members = Substitute.For<ICardiMemberService>();
    private readonly IUserContext _userContext = Substitute.For<IUserContext>();
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _organizationId = Guid.NewGuid();

    [Fact]
    public async Task APlanWithNoRoomLeft_Is422_WithTheServicesReason()
    {
        const string reason = "This family's plan covers 3 CardiMembers, and you're already watching 3.";
        _members.CreateCardiMemberAsync(
                _organizationId, _userId, Arg.Any<CreateCardiMemberRequest>(), Arg.Any<string?>())
            .ThrowsAsync(new FamilyRuleException(FamilyRuleException.CardiMemberLimitReached, reason));

        var result = await CreateSut().CreateCardiMember(new CreateCardiMemberRequest { Name = "Margaret Doe" });

        var refused = Assert.IsType<ObjectResult>(result.Result);
        // 422, as FamiliesController and FamilyJoinController return for the same exception.
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, refused.StatusCode);
        Assert.Equal(reason, Assert.IsType<ErrorResponse>(refused.Value).Message);
    }

    private OnboardingController CreateSut()
    {
        _userContext.IsAuthenticated.Returns(true);
        _userContext.UserId.Returns(_userId);
        _userContext.OrganizationId.Returns(_organizationId);

        var memberValidator = Substitute.For<IValidator<CreateCardiMemberRequest>>();
        memberValidator.ValidateAsync(Arg.Any<CreateCardiMemberRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ValidationResult());

        var guestFamilies = Substitute.For<IGuestFamilyProvisioner>();
        guestFamilies.ResolveHomeOrganizationAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(_organizationId);

        return new OnboardingController(
            _userContext,
            Substitute.For<ILogger<OnboardingController>>(),
            Substitute.For<IUserService>(),
            _members,
            Substitute.For<IOnboardingService>(),
            Substitute.For<IValidator<CreateOrganizationRequest>>(),
            memberValidator,
            guestFamilies)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }
}
