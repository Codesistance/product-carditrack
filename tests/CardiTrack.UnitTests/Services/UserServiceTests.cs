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
