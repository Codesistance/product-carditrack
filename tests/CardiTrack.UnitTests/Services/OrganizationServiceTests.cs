using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using NSubstitute;

namespace CardiTrack.UnitTests.Services;

public class OrganizationServiceTests
{
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IOrganizationRepository _organizations = Substitute.For<IOrganizationRepository>();
    private readonly ISubscriptionService _subscriptions = Substitute.For<ISubscriptionService>();

    public OrganizationServiceTests()
    {
        _unitOfWork.Organizations.Returns(_organizations);
    }

    private OrganizationService CreateSut() => new(_unitOfWork, _subscriptions);

    private static Subscription BuildTrialSubscription(Guid organizationId) => new()
    {
        OrganizationId = organizationId,
        Tier = SubscriptionTier.Complete,
        Status = SubscriptionStatus.Trial,
        TrialEndDate = DateTime.UtcNow.AddDays(30),
        MaxCardiMembers = 5,
        MaxUsers = 1,
    };

    // The Create_* tests are gone with OrganizationService.CreateOrganizationAsync, removed
    // 2026-09-22 along with POST /onboarding/organization. Creating a family now happens only
    // inside OnboardingService.SetupAsync, where it commits with the user and the membership in
    // one transaction — covered there, including the trial provisioning these asserted.

    [Fact]
    public async Task GetById_ReturnsNull_WhenOrganizationMissing()
    {
        var id = Guid.NewGuid();
        _organizations.GetWithSubscriptionAsync(id).Returns((Organization?)null);

        Assert.Null(await CreateSut().GetByIdAsync(id));
    }

    [Fact]
    public async Task GetById_MapsOrganizationAndSubscription()
    {
        var org = new Organization { Name = "Doe Family", Type = OrganizationType.Family, IsActive = true };
        org.Subscription = BuildTrialSubscription(org.Id);
        _organizations.GetWithSubscriptionAsync(org.Id).Returns(org);

        var response = await CreateSut().GetByIdAsync(org.Id);

        Assert.NotNull(response);
        Assert.Equal(org.Id, response!.Id);
        Assert.Equal("Doe Family", response.Name);
        Assert.Equal(org.Subscription.Id, response.Subscription!.Id);
        Assert.Equal(1, response.Subscription.MaxUsers);
    }

    [Fact]
    public async Task GetById_MapsNullSubscription()
    {
        var org = new Organization { Name = "Acme Care", Type = OrganizationType.Business };
        _organizations.GetWithSubscriptionAsync(org.Id).Returns(org);

        var response = await CreateSut().GetByIdAsync(org.Id);

        Assert.Null(response!.Subscription);
    }
}
