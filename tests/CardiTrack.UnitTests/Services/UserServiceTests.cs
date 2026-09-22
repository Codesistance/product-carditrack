using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.Exceptions;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.UnitTests.Notifications;
using NSubstitute;

namespace CardiTrack.UnitTests.Services;

public class UserServiceTests
{
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly IOrganizationRepository _organizations = Substitute.For<IOrganizationRepository>();
    private readonly IUserCardiMemberRepository _links = Substitute.For<IUserCardiMemberRepository>();
    private readonly IUserOrganizationRepository _memberships = Substitute.For<IUserOrganizationRepository>();
    private readonly IDeviceConnectionRepository _deviceConnections = Substitute.For<IDeviceConnectionRepository>();

    private readonly Guid _userId = Guid.NewGuid();

    public UserServiceTests()
    {
        _unitOfWork.Users.Returns(_users);
        _unitOfWork.Organizations.Returns(_organizations);
        _unitOfWork.UserCardiMembers.Returns(_links);
        _unitOfWork.UserOrganizations.Returns(_memberships);
        _unitOfWork.DeviceConnections.Returns(_deviceConnections);
        _links.GetByUserIdAsync(_userId).Returns([]);
        _deviceConnections.AnyActiveForCardiMembersAsync(Arg.Any<IEnumerable<Guid>>()).Returns(false);
    }

    private UserService CreateSut() => new(_unitOfWork, new NoOpNotificationGapResolver());

    // The CreateUser_* tests that stood here are gone with the endpoint they covered
    // (POST /onboarding/user, removed 2026-09-22). Nothing they asserted is untested: the
    // verification claim, the duplicate-email refusal, the concurrent-insert race, the rethrow on
    // an unrelated save failure and "the person who starts a family is its admin" all live on the
    // surviving single-call path and are covered by OnboardingServiceTests. The three that pinned
    // who may claim an organization are moot rather than merely moved — the seam they guarded no
    // longer exists.

    [Fact]
    public async Task OnboardingStatus_SyncsVerificationWhenTheClaimFlips()
    {
        var user = ExistingUser(emailVerified: false);
        _users.GetByIdAsync(_userId).Returns(user);

        await CreateSut().GetOnboardingStatusAsync(_userId, emailVerifiedClaim: true);

        Assert.True(user.EmailVerified);
        await _unitOfWork.Received(1).SaveChangesAsync();
    }

    [Fact]
    public async Task OnboardingStatus_DoesNotSaveWhenClaimMatchesOrIsAbsent()
    {
        var user = ExistingUser(emailVerified: true);
        _users.GetByIdAsync(_userId).Returns(user);

        var sut = CreateSut();
        await sut.GetOnboardingStatusAsync(_userId, emailVerifiedClaim: true);
        await sut.GetOnboardingStatusAsync(_userId, emailVerifiedClaim: null);

        Assert.True(user.EmailVerified);
        await _unitOfWork.DidNotReceive().SaveChangesAsync();
    }

    [Fact]
    public async Task OnboardingStatus_ReportsDeviceConnected_WhenAMemberHasAnActiveConnection()
    {
        var memberId = Guid.NewGuid();
        _users.GetByIdAsync(_userId).Returns(ExistingUser(emailVerified: true));
        _links.GetByUserIdAsync(_userId).Returns([new UserCardiMember { UserId = _userId, CardiMemberId = memberId }]);
        _deviceConnections
            .AnyActiveForCardiMembersAsync(Arg.Is<IEnumerable<Guid>>(ids => ids != null && ids.Contains(memberId)))
            .Returns(true);

        var status = await CreateSut().GetOnboardingStatusAsync(_userId);

        Assert.True(status.HasDeviceConnected);
        Assert.Equal(7, status.CurrentStep);
        Assert.Equal("Choose how you'd like to be notified", status.NextStepMessage);
    }

    [Fact]
    public async Task OnboardingStatus_ReportsNoDevice_WhenMembersHaveNoActiveConnections()
    {
        _users.GetByIdAsync(_userId).Returns(ExistingUser(emailVerified: true));
        _links.GetByUserIdAsync(_userId).Returns([new UserCardiMember { UserId = _userId, CardiMemberId = Guid.NewGuid() }]);

        var status = await CreateSut().GetOnboardingStatusAsync(_userId);

        Assert.False(status.HasDeviceConnected);
        Assert.Equal(6, status.CurrentStep);
        Assert.Equal("Now let's connect a health device", status.NextStepMessage);
    }

    [Fact]
    public async Task HasDismissedHealthDataDisclosure_ReturnsFalse_WhenNeverDismissed()
    {
        var user = ExistingUser(emailVerified: true);
        _users.GetByAuth0UserIdAsync("auth0|abc").Returns(user);

        Assert.False(await CreateSut().HasDismissedHealthDataDisclosureAsync("auth0|abc"));
    }

    [Fact]
    public async Task HasDismissedHealthDataDisclosure_ReturnsTrue_WhenAlreadyDismissed()
    {
        var user = ExistingUser(emailVerified: true);
        user.HealthDataDisclosureDismissedDate = DateTime.UtcNow.AddDays(-1);
        _users.GetByAuth0UserIdAsync("auth0|abc").Returns(user);

        Assert.True(await CreateSut().HasDismissedHealthDataDisclosureAsync("auth0|abc"));
    }

    [Fact]
    public async Task HasDismissedHealthDataDisclosure_ReturnsFalse_WhenUserUnknown()
    {
        _users.GetByAuth0UserIdAsync("auth0|missing").Returns((User?)null);

        Assert.False(await CreateSut().HasDismissedHealthDataDisclosureAsync("auth0|missing"));
    }

    [Fact]
    public async Task DismissHealthDataDisclosure_SetsUtcTimestampAndSaves()
    {
        var user = ExistingUser(emailVerified: true);
        _users.GetByAuth0UserIdAsync("auth0|abc").Returns(user);
        var before = DateTime.UtcNow;

        var result = await CreateSut().DismissHealthDataDisclosureAsync("auth0|abc");

        Assert.True(result);
        // Recorded through the repository's conditional update — one statement that only lands
        // when nothing is stamped yet — never by read-then-save on the entity.
        await _users.Received(1).TryRecordHealthDataDisclosureDismissalAsync(
            "auth0|abc",
            Arg.Is<DateTime>(d => d.Kind == DateTimeKind.Utc && d >= before && d <= DateTime.UtcNow));
        await _unitOfWork.DidNotReceive().SaveChangesAsync();
    }

    [Fact]
    public async Task DismissHealthDataDisclosure_KeepsFirstTimestampAndDoesNotResave()
    {
        var firstDismissal = DateTime.UtcNow.AddDays(-3);
        var user = ExistingUser(emailVerified: true);
        user.HealthDataDisclosureDismissedDate = firstDismissal;
        _users.GetByAuth0UserIdAsync("auth0|abc").Returns(user);

        var result = await CreateSut().DismissHealthDataDisclosureAsync("auth0|abc");

        Assert.True(result);
        Assert.Equal(firstDismissal, user.HealthDataDisclosureDismissedDate);
        await _unitOfWork.DidNotReceive().SaveChangesAsync();
        await _users.DidNotReceiveWithAnyArgs().TryRecordHealthDataDisclosureDismissalAsync(default!, default);
    }

    [Fact]
    public async Task DismissHealthDataDisclosure_ReturnsFalseAndDoesNotSave_WhenUserUnknown()
    {
        _users.GetByAuth0UserIdAsync("auth0|missing").Returns((User?)null);

        var result = await CreateSut().DismissHealthDataDisclosureAsync("auth0|missing");

        Assert.False(result);
        await _unitOfWork.DidNotReceive().SaveChangesAsync();
    }

    private User ExistingUser(bool emailVerified) => new()
    {
        Id = _userId,
        Auth0UserId = "auth0|abc",
        Email = "carer@example.com",
        Name = "Jane Carer",
        OrganizationId = Guid.NewGuid(),
        IsActive = true,
        EmailVerified = emailVerified,
    };
}
