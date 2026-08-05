using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using NSubstitute;

namespace CardiTrack.UnitTests.Services;

public class OnboardingServiceTests
{
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IOrganizationRepository _organizations = Substitute.For<IOrganizationRepository>();
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly ISubscriptionService _subscriptions = Substitute.For<ISubscriptionService>();

    public OnboardingServiceTests()
    {
        _unitOfWork.Organizations.Returns(_organizations);
        _unitOfWork.Users.Returns(_users);
    }

    private OnboardingService CreateSut() => new(_unitOfWork, _subscriptions);

    private static OnboardingSetupRequest BuildRequest() => new()
    {
        Organization = new CreateOrganizationRequest { Name = "Doe Family", Type = OrganizationType.Family },
        User = new OnboardingSetupUserRequest
        {
            Email = "jane@doe.com",
            Name = "Jane Doe",
            Role = UserRole.Member,
            TimeZoneId = "Europe/London",
        },
    };

    [Fact]
    public async Task Setup_CreatesOrgSubscriptionAndUser_WithSingleSave()
    {
        Organization? savedOrg = null;
        User? savedUser = null;
        await _organizations.AddAsync(Arg.Do<Organization>(o => savedOrg = o));
        await _users.AddAsync(Arg.Do<User>(u => savedUser = u));

        await CreateSut().SetupAsync(BuildRequest(), "auth0|jane", emailVerified: true);

        Assert.NotNull(savedOrg);
        Assert.Equal("Doe Family", savedOrg!.Name);
        await _subscriptions.Received(1).CreateTrialSubscriptionAsync(savedOrg.Id, OrganizationType.Family);
        Assert.NotNull(savedUser);
        Assert.Equal(savedOrg.Id, savedUser!.OrganizationId);
        Assert.Equal("auth0|jane", savedUser.Auth0UserId);
        Assert.True(savedUser.EmailVerified);
        Assert.Equal("Europe/London", savedUser.TimeZoneId);
        // Atomicity hinges on everything committing in one SaveChanges.
        await _unitOfWork.Received(1).SaveChangesAsync();
    }

    [Fact]
    public async Task Setup_MapsBothResponses_FromReloadedOrganization()
    {
        await _organizations.AddAsync(Arg.Do<Organization>(o =>
        {
            o.Subscription = new Subscription
            {
                OrganizationId = o.Id,
                Tier = SubscriptionTier.Complete,
                Status = SubscriptionStatus.Trial,
                MaxCardiMembers = 5,
                MaxUsers = 1,
            };
            _organizations.GetWithSubscriptionAsync(o.Id).Returns(o);
        }));

        var response = await CreateSut().SetupAsync(BuildRequest(), "auth0|jane", emailVerified: null);

        Assert.Equal("Doe Family", response.Organization.Name);
        Assert.NotNull(response.Organization.Subscription);
        Assert.Equal(SubscriptionStatus.Trial, response.Organization.Subscription!.Status);
        Assert.Equal("jane@doe.com", response.User.Email);
        Assert.Equal(response.Organization.Id, response.User.OrganizationId);
        Assert.True(response.User.IsActive);
    }

    [Fact]
    public async Task Setup_ReturnsExistingAccount_WithoutCreatingAnything_WhenAlreadyOnboarded()
    {
        var org = new Organization { Name = "Doe Family", Type = OrganizationType.Family };
        var user = new User
        {
            Auth0UserId = "auth0|jane",
            Email = "jane@doe.com",
            Name = "Jane Doe",
            OrganizationId = org.Id,
        };
        _users.GetByAuth0UserIdAsync("auth0|jane").Returns(user);
        _organizations.GetWithSubscriptionAsync(org.Id).Returns(org);

        var response = await CreateSut().SetupAsync(BuildRequest(), "auth0|jane", emailVerified: true);

        Assert.Equal(org.Id, response.Organization.Id);
        Assert.Equal(user.Id, response.User.Id);
        await _organizations.DidNotReceive().AddAsync(Arg.Any<Organization>());
        await _users.DidNotReceive().AddAsync(Arg.Any<User>());
        await _subscriptions.DidNotReceive().CreateTrialSubscriptionAsync(Arg.Any<Guid>(), Arg.Any<OrganizationType>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync();
    }

    [Fact]
    public async Task Setup_DefaultsEmailVerifiedToFalse_WhenClaimAbsent()
    {
        User? savedUser = null;
        await _users.AddAsync(Arg.Do<User>(u => savedUser = u));

        await CreateSut().SetupAsync(BuildRequest(), "auth0|jane", emailVerified: null);

        Assert.False(savedUser!.EmailVerified);
    }
}
