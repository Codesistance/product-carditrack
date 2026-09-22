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

    public async Task<OrganizationResponse> CreateOrganizationAsync(CreateOrganizationRequest request)
    {
        var organization = new Organization
        {
            Name = request.Name,
            FamilyId = FamilyIdentifier.Mint(),
            Type = request.Type,
            IsActive = true
        };

        await _unitOfWork.Organizations.AddAsync(organization);

        // Create trial subscription (Step 3 in onboarding)
        await _subscriptionService.CreateTrialSubscriptionAsync(organization.Id, request.Type);

        await _unitOfWork.SaveChangesAsync();

        // Fetch with subscription to return complete response
        var orgWithSubscription = await _unitOfWork.Organizations.GetWithSubscriptionAsync(organization.Id);

        return new OrganizationResponse
        {
            Id = organization.Id,
            Name = organization.Name,
            Type = organization.Type,
            IsActive = organization.IsActive,
            CreatedDate = organization.CreatedDate,
            Subscription = orgWithSubscription?.Subscription != null ? new SubscriptionResponse
            {
                Id = orgWithSubscription.Subscription.Id,
                Tier = orgWithSubscription.Subscription.Tier,
                Status = orgWithSubscription.Subscription.Status,
                StartDate = orgWithSubscription.Subscription.StartDate,
                TrialEndDate = orgWithSubscription.Subscription.TrialEndDate,
                MaxCardiMembers = orgWithSubscription.Subscription.MaxCardiMembers,
                MaxUsers = orgWithSubscription.Subscription.MaxUsers
            } : null
        };
    }

    public async Task<OrganizationResponse?> GetByIdAsync(Guid id)
    {
        var org = await _unitOfWork.Organizations.GetWithSubscriptionAsync(id);
        if (org == null) return null;

        return new OrganizationResponse
        {
            Id = org.Id,
            Name = org.Name,
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
