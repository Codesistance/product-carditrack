using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Domain.Entities;

namespace CardiTrack.Application.Services;

public class OrganizationService : IOrganizationService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ISubscriptionService _subscriptionService;

    public OrganizationService(IUnitOfWork unitOfWork, ISubscriptionService subscriptionService)
    {
        _unitOfWork = unitOfWork;
        _subscriptionService = subscriptionService;
    }

    public async Task<OrganizationResponse?> GetByIdAsync(Guid id)
    {
        var org = await _unitOfWork.Organizations.GetWithSubscriptionAsync(id);
        if (org == null) return null;

        return new OrganizationResponse
        {
            Id = org.Id,
            Name = org.Name,
            FamilyId = FamilyIdentifier.ToDisplay(org.FamilyId),
            Type = org.Type,
            IsActive = org.IsActive,
            CreatedDate = org.CreatedDate,
            Subscription = org.Subscription != null ? new SubscriptionResponse
            {
                Id = org.Subscription.Id,
                Tier = org.Subscription.Tier,
                Status = org.Subscription.Status,
                StartDate = org.Subscription.StartDate,
                TrialEndDate = org.Subscription.TrialEndDate,
                MaxCardiMembers = org.Subscription.MaxCardiMembers,
                MaxUsers = org.Subscription.MaxUsers
            } : null
        };
    }
}
