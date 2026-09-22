using CardiTrack.API.Controllers;
using CardiTrack.API.Infrastructure.UserContext;
using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Interfaces.Services;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace CardiTrack.IntegrationTests.Controllers;

/// <summary>
/// What the create action does with the <c>Idempotency-Key</c> header: passes it on, leaves it
/// null when absent, and refuses one longer than the column can hold.
/// </summary>
/// <remarks>
/// The 64-character limit was only exercised by a service-level test sending 65 characters to
/// PostgreSQL, which proves the column refuses it but says nothing about the action that is
/// supposed to refuse it first. Removing or reordering the guard would have left the documented
/// 400 untested.
/// </remarks>
public class OnboardingIdempotencyKeyTests
{
    private readonly ICardiMemberService _members = Substitute.For<ICardiMemberService>();
    private readonly IUserContext _userContext = Substitute.For<IUserContext>();
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _organizationId = Guid.NewGuid();
    private readonly DefaultHttpContext _httpContext = new();

    [Fact]
    public async Task AnOverlongKey_Is400_AndNeverReachesTheService()
    {
        _httpContext.Request.Headers["Idempotency-Key"] = new string('k', 65);

        var result = await CreateSut().CreateCardiMember(Request());

        Assert.Equal(StatusCodes.Status400BadRequest, ((ObjectResult)result.Result!).StatusCode);
        await _members.DidNotReceiveWithAnyArgs().CreateCardiMemberAsync(default, default, null!, null);
    }

    [Fact]
    public async Task AKeyAtTheLimit_IsAccepted_AndHandedToTheService()
    {
        var key = new string('k', 64);
        _httpContext.Request.Headers["Idempotency-Key"] = key;
        Created();

        await CreateSut().CreateCardiMember(Request());

        await _members.Received(1).CreateCardiMemberAsync(
            _organizationId, _userId, Arg.Any<CreateCardiMemberRequest>(), key);
    }

    /// <summary>
    /// No header, no key — not an empty string. The service reads null as "behave as you always
    /// did", and an empty key would be a value it would try to store and match on.
    /// </summary>
    [Fact]
    public async Task NoHeader_PassesNoKey()
    {
        Created();

        await CreateSut().CreateCardiMember(Request());

        await _members.Received(1).CreateCardiMemberAsync(
            _organizationId, _userId, Arg.Any<CreateCardiMemberRequest>(), null);
    }

    /// <summary>
    /// A header present but blank is the same as absent: whitespace is not a name for an attempt,
    /// and storing it would let two unrelated blank-header creates collide on the unique index.
    /// </summary>
    [Fact]
    public async Task ABlankHeader_PassesNoKey()
    {
        _httpContext.Request.Headers["Idempotency-Key"] = "   ";
        Created();

        await CreateSut().CreateCardiMember(Request());

        await _members.Received(1).CreateCardiMemberAsync(
            _organizationId, _userId, Arg.Any<CreateCardiMemberRequest>(), null);
    }

    private void Created() =>
        _members.CreateCardiMemberAsync(
                _organizationId, _userId, Arg.Any<CreateCardiMemberRequest>(), Arg.Any<string?>())
            .Returns(new CardiMemberResponse { Id = Guid.NewGuid(), Name = "Margaret Doe" });

    private static CreateCardiMemberRequest Request() => new() { Name = "Margaret Doe" };

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
            Substitute.For<IUserService>(),
            _members,
            Substitute.For<IOnboardingService>(),
            Substitute.For<IValidator<CreateOrganizationRequest>>(),
            memberValidator,
            GuestFamilies(_organizationId))
        {
            ControllerContext = new ControllerContext { HttpContext = _httpContext },
        };
    }

    /// <summary>
    /// Stands in for the provisioner that gives a guest a family on their first member. These
    /// tests are about the member-creation path for somebody who already has one, so it simply
    /// hands back the organization they are in.
    /// </summary>
    private static IGuestFamilyProvisioner GuestFamilies(Guid organizationId)
    {
        var provisioner = Substitute.For<IGuestFamilyProvisioner>();
        provisioner.ResolveHomeOrganizationAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(organizationId);
        return provisioner;
    }
}
