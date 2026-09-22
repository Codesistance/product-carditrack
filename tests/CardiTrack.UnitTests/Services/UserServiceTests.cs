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
    public async Task CreateUser_ThrowsDuplicateEmail_WhenEmailOwnedByDifferentSub()
    {
        _users.GetByEmailAsync("carer@example.com").Returns(
            new User { Auth0UserId = "auth0|someone-else", Email = "carer@example.com" });

        await Assert.ThrowsAsync<DuplicateEmailException>(() =>
            CreateSut().CreateUserAsync(Request(emailVerified: true)));

        await _users.DidNotReceive().AddAsync(Arg.Any<User>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync();
    }

    [Fact]
    public async Task CreateUser_ConcurrentInsert_EmailRace_ThrowsDuplicateEmail_InsteadOfRethrowing()
    {
        // The insert loses a race on the Email unique index: the pre-check missed
        // (email unowned yet), but by the time SaveChanges runs a concurrent request
        // has already claimed it under a different sub.
        _users.GetByEmailAsync("carer@example.com").Returns(
            _ => (User?)null,
            _ => new User { Auth0UserId = "auth0|winner", Email = "carer@example.com" });
        _unitOfWork.SaveChangesAsync().Returns<Task<int>>(_ => throw new InvalidOperationException("unique violation"));

        await Assert.ThrowsAsync<DuplicateEmailException>(() =>
            CreateSut().CreateUserAsync(Request(emailVerified: true)));
    }

    [Fact]
    public async Task CreateUser_Rethrows_WhenSaveFails_AndNoConcurrentOwnerExists()
    {
        _users.GetByEmailAsync("carer@example.com").Returns((User?)null);
        _unitOfWork.SaveChangesAsync().Returns<Task<int>>(_ => throw new InvalidOperationException("db down"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateSut().CreateUserAsync(Request(emailVerified: true)));
    }

    /// <summary>
    /// The legacy two-call create path admits its caller to the family they just created in the
    /// preceding call — one with nobody in it yet — and makes them its admin.
    /// </summary>
    [Fact]
    public async Task CreateUser_MakesTheCreatorAdminOfTheEmptyOrganizationTheyJustMade()
    {
        User? savedUser = null;
        UserOrganization? savedMembership = null;
        await _users.AddAsync(Arg.Do<User>(u => savedUser = u));
        await _memberships.AddAsync(Arg.Do<UserOrganization>(m => savedMembership = m));
        var request = Request(emailVerified: true);
        _memberships.GetByOrganizationIdAsync(request.OrganizationId).Returns([]);

        await CreateSut().CreateUserAsync(request);

        Assert.NotNull(savedMembership);
        Assert.Equal(savedUser!.Id, savedMembership!.UserId);
        Assert.Equal(request.OrganizationId, savedMembership.OrganizationId);
        // Assigned, not accepted from the body — see the test below for why that distinction is
        // the whole point.
        Assert.Equal(UserRole.Admin, savedMembership.Role);
    }

    /// <summary>
    /// The escalation this path used to allow, and the reason the role is no longer taken from
    /// the request body.
    /// </summary>
    /// <remarks>
    /// Naming an organization with <c>Role = Admin</c> was harmless while <c>User.Role</c> was a
    /// column nothing authorized against. It stopped being harmless the moment
    /// <c>UserOrganization.Role</c> became the fact <c>FamilyService</c> and
    /// <c>FamilyJoinService</c> gate admin operations on — at which point any authenticated caller
    /// who knew a family's id, a removed caregiver included, could take it over.
    /// </remarks>
    [Fact]
    public async Task CreateUser_CannotJoinAFamilyThatAlreadyHasMembers_LetAloneRunIt()
    {
        UserOrganization? savedMembership = null;
        await _memberships.AddAsync(Arg.Do<UserOrganization>(m => savedMembership = m));

        var request = Request(emailVerified: true);
        request.Role = UserRole.Admin;
        _memberships.GetByOrganizationIdAsync(request.OrganizationId).Returns(
        [
            new UserOrganization
            {
                UserId = Guid.NewGuid(),
                OrganizationId = request.OrganizationId,
                Role = UserRole.Admin,
                IsActive = true,
            },
        ]);

        await CreateSut().CreateUserAsync(request);

        // No membership at all. Joining a family that exists is an ask an admin answers, not
        // something a request body decides.
        Assert.Null(savedMembership);
    }

    [Fact]
    public async Task CreateUser_RefusedAFamily_DoesNotKeepItAsTheirHomeOne()
    {
        User? savedUser = null;
        await _users.AddAsync(Arg.Do<User>(u => savedUser = u));

        var request = Request(emailVerified: true);
        _memberships.GetByOrganizationIdAsync(request.OrganizationId).Returns(
        [
            new UserOrganization
            {
                UserId = Guid.NewGuid(),
                OrganizationId = request.OrganizationId,
                Role = UserRole.Admin,
                IsActive = true,
            },
        ]);

        await CreateSut().CreateUserAsync(request);

        // OrganizationId is what the rest of the product reads as "this account's own family".
        // Left pointing at one they were just refused, it would grant by the back door exactly
        // what the membership check refused at the front.
        Assert.NotNull(savedUser);
        Assert.Null(savedUser!.OrganizationId);
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
