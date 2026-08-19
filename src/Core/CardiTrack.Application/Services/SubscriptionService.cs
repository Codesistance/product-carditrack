using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Services;

public class SubscriptionService : ISubscriptionService
{
    private readonly IUnitOfWork _unitOfWork;

    public SubscriptionService(IUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    public async Task CreateTrialSubscriptionAsync(Guid organizationId, OrganizationType orgType)
    {
        var subscription = new Subscription
        {
            OrganizationId = organizationId,
            Tier = SubscriptionTier.Complete,
            Status = SubscriptionStatus.Trial,
            StartDate = DateTime.UtcNow,
            TrialEndDate = DateTime.UtcNow.AddDays(30),
            BillingCycle = BillingCycle.Monthly,
            Price = 0,
            Currency = "USD",
            // Must match what the Complete tier actually allows, or the trial becomes a
            // downgrade cliff the moment limits are enforced: a triallist who added the
            // trial's allowance could not then convert without deleting members. Complete
            // Care allowed 5 until the 2026-08-18 repricing and allows 3 now.
            MaxCardiMembers = orgType == OrganizationType.Family ? 3 : 50,
            MaxUsers = orgType == OrganizationType.Family ? 1 : 20,
            Features = "{\"deviceTypes\":[\"All\"],\"alertTypes\":[\"All\"],\"realTimeSync\":true}"
        };

        await _unitOfWork.Subscriptions.AddAsync(subscription);
    }
}
