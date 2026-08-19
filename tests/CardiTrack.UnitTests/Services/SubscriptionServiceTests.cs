using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using NSubstitute;

namespace CardiTrack.UnitTests.Services;

public class SubscriptionServiceTests
{
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly ISubscriptionRepository _subscriptions = Substitute.For<ISubscriptionRepository>();

    private readonly Guid _organizationId = Guid.NewGuid();

    public SubscriptionServiceTests()
    {
        _unitOfWork.Subscriptions.Returns(_subscriptions);
    }

    private async Task<Subscription> CreateTrialAsync(OrganizationType orgType)
    {
        Subscription? saved = null;
        await _subscriptions.AddAsync(Arg.Do<Subscription>(s => saved = s));

        await new SubscriptionService(_unitOfWork).CreateTrialSubscriptionAsync(_organizationId, orgType);

        Assert.NotNull(saved);
        return saved!;
    }

    [Fact]
    public async Task Trial_IsFreeCompleteTier_ForThirtyDays()
    {
        var before = DateTime.UtcNow;
        var subscription = await CreateTrialAsync(OrganizationType.Family);
        var after = DateTime.UtcNow;

        Assert.Equal(_organizationId, subscription.OrganizationId);
        Assert.Equal(SubscriptionTier.Complete, subscription.Tier);
        Assert.Equal(SubscriptionStatus.Trial, subscription.Status);
        Assert.Equal(BillingCycle.Monthly, subscription.BillingCycle);
        Assert.Equal(0, subscription.Price);
        Assert.Equal("USD", subscription.Currency);
        Assert.InRange(subscription.TrialEndDate!.Value, before.AddDays(30), after.AddDays(30));
        Assert.Contains("\"realTimeSync\":true", subscription.Features);
    }

    /// <summary>
    /// The family trial must not grant more members than the tier it provisions. It provisions
    /// <see cref="SubscriptionTier.Complete"/>, which allowed 5 until the 2026-08-18 repricing
    /// and allows 3 now — a trial above the tier ceiling becomes a downgrade cliff once limits
    /// are enforced, because the triallist cannot convert without deleting members.
    /// </summary>
    [Fact]
    public async Task FamilyTrial_LimitsToThreeMembersAndOneUser()
    {
        var subscription = await CreateTrialAsync(OrganizationType.Family);

        Assert.Equal(3, subscription.MaxCardiMembers);
        Assert.Equal(1, subscription.MaxUsers);
    }

    [Fact]
    public async Task BusinessTrial_LimitsToFiftyMembersAndTwentyUsers()
    {
        var subscription = await CreateTrialAsync(OrganizationType.Business);

        Assert.Equal(50, subscription.MaxCardiMembers);
        Assert.Equal(20, subscription.MaxUsers);
    }

    [Fact]
    public async Task Trial_DoesNotSave_CallerOwnsTheUnitOfWork()
    {
        await CreateTrialAsync(OrganizationType.Family);

        await _unitOfWork.DidNotReceive().SaveChangesAsync();
    }
}
