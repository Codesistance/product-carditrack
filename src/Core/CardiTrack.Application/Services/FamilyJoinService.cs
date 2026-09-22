using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Services;

/// <inheritdoc cref="IFamilyJoinService"/>
public class FamilyJoinService : IFamilyJoinService
{
    /// <summary>
    /// How long a request waits for an answer. Seven days, matching the invitation's lifetime:
    /// the two are the same promise seen from opposite ends, and a family that takes a week to
    /// answer one should not answer the other faster.
    /// </summary>
    private static readonly TimeSpan RequestLifetime = TimeSpan.FromDays(7);

    private const string DeniedMessage = "Family not found";

    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;

    public FamilyJoinService(IUnitOfWork unitOfWork, TimeProvider? timeProvider = null)
    {
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<FamilyJoinRequestReceipt> RequestAsync(
        Guid requestingUserId, JoinFamilyRequest request, CancellationToken ct = default)
    {
        // Every early return below hands back the same empty receipt. A malformed code, an unknown
        // family, and one the asker is already in are indistinguishable from outside — which is
        // what stops a walk through the code space from mapping which families exist.
        var empty = new FamilyJoinRequestReceipt(null, null);

        if (requestingUserId == Guid.Empty)
            return empty;

        var normalized = FamilyIdentifier.NormalizeOrNull(request.FamilyId);
        if (normalized is null)
            return empty;

        var organization = await _unitOfWork.Organizations
            .FirstOrDefaultAsync(o => o.FamilyId == normalized && o.IsActive);
        if (organization is null)
            return empty;

        var existingMembership = await _unitOfWork.UserOrganizations.GetAsync(
            requestingUserId, organization.Id);
        if (existingMembership is { IsActive: true })
            return empty;

        var now = _timeProvider.GetUtcNow().UtcDateTime;

        // Asking twice is a double tap, not a second request. Returning the outstanding one keeps
        // an admin's queue to one row per person and keeps the asker's screen honest.
        var live = await _unitOfWork.FamilyJoinRequests.GetLiveAsync(requestingUserId, organization.Id, now, ct);
        if (live is not null)
            return new FamilyJoinRequestReceipt(live.Id, live.ExpiresAt);

        var joinRequest = new FamilyJoinRequest
        {
            OrganizationId = organization.Id,
            RequestedByUserId = requestingUserId,
            Status = FamilyJoinRequestStatus.Pending,
            ExpiresAt = now.Add(RequestLifetime),
        };

        await _unitOfWork.FamilyJoinRequests.AddAsync(joinRequest);
        await _unitOfWork.SaveChangesAsync();

        return new FamilyJoinRequestReceipt(joinRequest.Id, joinRequest.ExpiresAt);
    }

    public async Task<IReadOnlyList<FamilyJoinRequestSummary>> GetMineAsync(
        Guid requestingUserId, CancellationToken ct = default)
    {
        if (requestingUserId == Guid.Empty)
            return [];

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var requests = await _unitOfWork.FamilyJoinRequests.GetForUserAsync(requestingUserId, ct);

        var summaries = new List<FamilyJoinRequestSummary>(requests.Count);
        foreach (var request in requests)
        {
            var organization = await _unitOfWork.Organizations.GetByIdAsync(request.OrganizationId);
            summaries.Add(new FamilyJoinRequestSummary(
                request.Id,
                organization?.Name ?? string.Empty,
                ToWireStatus(request, now),
                request.CreatedDate,
                request.ExpiresAt));
        }

        return summaries;
    }

    public async Task WithdrawAsync(Guid requestingUserId, Guid requestId, CancellationToken ct = default)
    {
        var request = await _unitOfWork.FamilyJoinRequests.GetByIdAsync(requestId);
        if (request is null || request.RequestedByUserId != requestingUserId)
            throw new KeyNotFoundException("Request not found");

        await _unitOfWork.FamilyJoinRequests.TryResolveAsync(
            requestId,
            FamilyJoinRequestStatus.Withdrawn,
            requestingUserId,
            _timeProvider.GetUtcNow().UtcDateTime,
            ct);
    }

    public async Task<IReadOnlyList<PendingJoinRequest>> GetPendingAsync(
        Guid requestingUserId, Guid organizationId, CancellationToken ct = default)
    {
        await RequireAdminAsync(requestingUserId, organizationId);

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var requests = await _unitOfWork.FamilyJoinRequests.GetPendingForOrganizationAsync(
            organizationId, now, ct);

        var pending = new List<PendingJoinRequest>(requests.Count);
        foreach (var request in requests)
        {
            var user = await _unitOfWork.Users.GetByIdAsync(request.RequestedByUserId);
            if (user is null || !user.IsActive)
                continue;

            pending.Add(new PendingJoinRequest(
                request.Id, user.Id, user.Name, user.Email, request.CreatedDate, request.ExpiresAt));
        }

        return pending;
    }

    public async Task ApproveAsync(
        Guid requestingUserId,
        Guid organizationId,
        Guid requestId,
        ApproveJoinRequest decision,
        CancellationToken ct = default)
    {
        await RequireAdminAsync(requestingUserId, organizationId);

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var request = await _unitOfWork.FamilyJoinRequests.GetByIdAsync(requestId);
        if (request is null || request.OrganizationId != organizationId)
            throw new KeyNotFoundException("Request not found");

        if (request.Status != FamilyJoinRequestStatus.Pending || request.ExpiresAt <= now)
            throw new KeyNotFoundException("That request is no longer waiting for an answer.");

        // The asker must still be somebody. An account closed between asking and answering leaves
        // a row an admin could otherwise approve into a membership for nobody.
        var asker = await _unitOfWork.Users.GetByIdAsync(request.RequestedByUserId);
        if (asker is null || !asker.IsActive)
            throw new KeyNotFoundException("That person's account is no longer active.");

        // Only members of this family may be granted through it. An id from somewhere else is a
        // mistake at best, so the approval fails rather than silently granting less than it says.
        var familyMembers = (await _unitOfWork.CardiMembers.GetByOrganizationIdAsync(organizationId))
            .ToDictionary(m => m.Id);
        foreach (var memberId in decision.CardiMemberIds)
        {
            if (!familyMembers.ContainsKey(memberId))
                throw new KeyNotFoundException("CardiMember not found");
        }

        if (await _unitOfWork.UserOrganizations.GetAsync(request.RequestedByUserId, organizationId)
                is not { IsActive: true })
        {
            await PlanLimits.RequireRoomForAnotherPersonAsync(_unitOfWork, organizationId, ct);
        }

        // Claim the request first. Two admins answering at once is settled here, before anything is
        // written, so the loser adds no second membership and no duplicate grants.
        if (!await _unitOfWork.FamilyJoinRequests.TryResolveAsync(
                requestId, FamilyJoinRequestStatus.Approved, requestingUserId, now, ct))
        {
            throw new KeyNotFoundException("That request is no longer waiting for an answer.");
        }

        var grantedRole = ResolveRole(decision.Role);
        await EnsureMembershipAsync(request.RequestedByUserId, organizationId, grantedRole, now);

        // Admitting somebody as admin hands them the family, and its plan, in the same act — a
        // family has exactly one admin, so the approver necessarily stops being it. Done here
        // rather than left to a follow-up call, because a moment with two admins is a moment where
        // "the admin pays" names nobody.
        if (grantedRole == UserRole.Admin)
        {
            var approver = await _unitOfWork.UserOrganizations.GetAsync(requestingUserId, organizationId);
            if (approver is not null)
            {
                approver.Role = UserRole.Member;
                approver.UpdatedDate = now;
                _unitOfWork.UserOrganizations.Update(approver);
            }
        }

        var existingLinks = (await _unitOfWork.UserCardiMembers.GetByUserIdAsync(request.RequestedByUserId))
            .ToDictionary(l => l.CardiMemberId);

        foreach (var memberId in decision.CardiMemberIds.Distinct())
        {
            if (existingLinks.TryGetValue(memberId, out var existing))
            {
                // Reactivating what they had rather than adding a second row, and leaving the flags
                // alone: an approval is permission to be here, not a licence to quietly widen
                // access somebody already had on different terms.
                existing.IsActive = true;
                existing.UpdatedDate = now;
                _unitOfWork.UserCardiMembers.Update(existing);
                continue;
            }

            await _unitOfWork.UserCardiMembers.AddAsync(new UserCardiMember
            {
                UserId = request.RequestedByUserId,
                CardiMemberId = memberId,
                RelationshipType = RelationshipType.Other,
                IsPrimaryCaregiver = false,
                CanViewHealthData = true,
                ReceiveAlerts = decision.ReceiveAlerts,
            });
        }

        await _unitOfWork.SaveChangesAsync();
    }

    public async Task DeclineAsync(
        Guid requestingUserId, Guid organizationId, Guid requestId, CancellationToken ct = default)
    {
        await RequireAdminAsync(requestingUserId, organizationId);

        var request = await _unitOfWork.FamilyJoinRequests.GetByIdAsync(requestId);
        if (request is null || request.OrganizationId != organizationId)
            throw new KeyNotFoundException("Request not found");

        await _unitOfWork.FamilyJoinRequests.TryResolveAsync(
            requestId,
            FamilyJoinRequestStatus.Declined,
            requestingUserId,
            _timeProvider.GetUtcNow().UtcDateTime,
            ct);
    }

    private async Task EnsureMembershipAsync(Guid userId, Guid organizationId, UserRole role, DateTime now)
    {
        var membership = await _unitOfWork.UserOrganizations.GetAsync(userId, organizationId);
        if (membership is null)
        {
            await _unitOfWork.UserOrganizations.AddAsync(new UserOrganization
            {
                UserId = userId,
                OrganizationId = organizationId,
                Role = role,
                JoinedDate = now,
            });
            return;
        }

        membership.IsActive = true;
        membership.Role = role;
        membership.JoinedDate = now;
        membership.UpdatedDate = now;
        _unitOfWork.UserOrganizations.Update(membership);
    }

    private async Task RequireAdminAsync(Guid requestingUserId, Guid organizationId)
    {
        if (requestingUserId == Guid.Empty)
            throw new KeyNotFoundException(DeniedMessage);

        var membership = await _unitOfWork.UserOrganizations.GetAsync(requestingUserId, organizationId);
        if (membership is not { IsActive: true, Role: UserRole.Admin })
            throw new KeyNotFoundException(DeniedMessage);
    }

    /// <summary>
    /// Member unless the admin explicitly said admin. Staff is never reachable: it belongs to the
    /// Enterprise offering and a family should not be able to mint one by sending the string.
    /// </summary>
    private static UserRole ResolveRole(string? role) =>
        string.Equals(role?.Trim(), "admin", StringComparison.OrdinalIgnoreCase)
            ? UserRole.Admin
            : UserRole.Member;

    private static string ToWireStatus(FamilyJoinRequest request, DateTime utcNow) =>
        request.Status == FamilyJoinRequestStatus.Pending && request.ExpiresAt <= utcNow
            ? "expired"
            : request.Status.ToString().ToLowerInvariant();
}
