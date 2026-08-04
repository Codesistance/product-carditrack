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

    [Fact]
    public async Task Create_PersistsOrganization_AndProvisionsTrialSubscription()
    {
        Organization? savedOrg = null;
        await _organizations.AddAsync(Arg.Do<Organization>(o => savedOrg = o));
        var request = new CreateOrganizationRequest { Name = "Doe Family", Type = OrganizationType.Family };

        await CreateSut().CreateOrganizationAsync(request);

        Assert.NotNull(savedOrg);
        Assert.Equal("Doe Family", savedOrg!.Name);
        Assert.Equal(OrganizationType.Family, savedOrg.Type);
        Assert.True(savedOrg.IsActive);
        await _subscriptions.Received(1).CreateTrialSubscriptionAsync(savedOrg.Id, OrganizationType.Family);
        await _unitOfWork.Received(1).SaveChangesAsync();
    }

    [Fact]
    public async Task Create_ReturnsResponseWithSubscription_WhenReloadedWithOne()
    {
        Organization? savedOrg = null;
        await _organizations.AddAsync(Arg.Do<Organization>(o =>
        {
            savedOrg = o;
            o.Subscription = BuildTrialSubscription(o.Id);
            _organizations.GetWithSubscriptionAsync(o.Id).Returns(o);
        }));

        var response = await CreateSut().CreateOrganizationAsync(
            new CreateOrganizationRequest { Name = "Doe Family", Type = OrganizationType.Family });

        Assert.Equal(savedOrg!.Id, response.Id);
        Assert.NotNull(response.Subscription);
        Assert.Equal(SubscriptionStatus.Trial, response.Subscription!.Status);
        Assert.Equal(SubscriptionTier.Complete, response.Subscription.Tier);
        Assert.Equal(5, response.Subscription.MaxCardiMembers);
    }

    [Fact]
    public async Task Create_ReturnsNullSubscription_WhenReloadFindsNone()
    {
        _organizations.GetWithSubscriptionAsync(Arg.Any<Guid>()).Returns((Organization?)null);

        var response = await CreateSut().CreateOrganizationAsync(
            new CreateOrganizationRequest { Name = "Acme Care", Type = OrganizationType.Business });

        Assert.Equal("Acme Care", response.Name);
        Assert.Null(response.Subscription);
    }

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
