using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using NSubstitute;

namespace CardiTrack.UnitTests.Services;

public class UserServiceTests
{
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly IOrganizationRepository _organizations = Substitute.For<IOrganizationRepository>();
    private readonly IUserCardiMemberRepository _links = Substitute.For<IUserCardiMemberRepository>();

    private readonly Guid _userId = Guid.NewGuid();

    public UserServiceTests()
    {
        _unitOfWork.Users.Returns(_users);
        _unitOfWork.Organizations.Returns(_organizations);
        _unitOfWork.UserCardiMembers.Returns(_links);
        _links.GetByUserIdAsync(_userId).Returns([]);
    }

    private UserService CreateSut() => new(_unitOfWork);

    private static CreateUserRequest Request(bool? emailVerified) => new()
    {
        Auth0UserId = "auth0|abc",
        Email = "carer@example.com",
        Name = "Jane Carer",
        OrganizationId = Guid.NewGuid(),
        EmailVerified = emailVerified,
    };

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(null, false)] // claim absent from the token => unverified, not assumed true
    public async Task CreateUser_StoresTheRealVerificationClaim(bool? claim, bool expected)
    {
        User? added = null;
        await _users.AddAsync(Arg.Do<User>(u => added = u));

        await CreateSut().CreateUserAsync(Request(claim));

        Assert.NotNull(added);
        Assert.Equal(expected, added!.EmailVerified);
    }

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
        Assert.NotNull(user.HealthDataDisclosureDismissedDate);
        Assert.InRange(user.HealthDataDisclosureDismissedDate!.Value, before, DateTime.UtcNow);
        await _unitOfWork.Received(1).SaveChangesAsync();
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
