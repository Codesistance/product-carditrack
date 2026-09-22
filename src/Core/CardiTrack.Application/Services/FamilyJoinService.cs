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

    private readonly IFamilyWriteGuard _guard;

    public FamilyJoinService(
        IUnitOfWork unitOfWork, IFamilyWriteGuard guard, TimeProvider? timeProvider = null)
    {
        _unitOfWork = unitOfWork;
        _guard = guard;
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

        // Family organizations only. Every organization is minted a code, business ones included,
        // but joining is a family contract: one admin who pays, a roster anybody in it can see,
        // and a role that grants a share of watching. A care home's roster is staff, governed by
        // an agreement rather than by whoever holds the code, so its code resolves to nothing
        // here — and does so through the same empty receipt as an unknown one, which is what keeps
        // "that code exists but is not joinable" from being an answer this endpoint gives.
        var organization = await _unitOfWork.Organizations
            .FirstOrDefaultAsync(o => o.FamilyId == normalized
                                      && o.Type == OrganizationType.Family
                                      && o.IsActive);
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

        // The read above and this insert are two statements, so two taps a moment apart can both
        // find nothing live. The repository settles that against the unique index and hands back
        // whichever request is now live, so the loser gets a receipt rather than a 500.
        var stored = await _unitOfWork.FamilyJoinRequests.AddOrGetLiveAsync(joinRequest, now, ct);

        return new FamilyJoinRequestReceipt(stored.Id, stored.ExpiresAt);
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
        // One transaction, held on the family, around everything below. Two things needed it.
        //
        // The claim used to commit on its own: TryResolveAsync is a conditional ExecuteUpdate, so
        // the request was already Approved before the membership and grants were written. Any
        // failure after that point left somebody approved with no access and no way to retry —
        // their request reads as answered.
        //
        // And the seat check is a count followed by an insert. Two admins approving two different
        // people at once would both see the last free place and both take it.
        await using var guarded = await BeginGuardedAsync(organizationId, ct);

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

        // The incumbent steps down before the new admin is written, and the demotion is saved on
        // its own. Same reason as FamilyService.TransferAdminAsync: the one-active-admin index is
        // checked per statement, so promoting first would pass through a state with two. Still one
        // transaction, so the handover remains all-or-nothing.
        if (grantedRole == UserRole.Admin)
        {
            var stepping = await _unitOfWork.UserOrganizations.GetAsync(requestingUserId, organizationId);
            if (stepping is not null)
            {
                stepping.Role = UserRole.Member;
                stepping.UpdatedDate = now;
                _unitOfWork.UserOrganizations.Update(stepping);
                await _unitOfWork.SaveChangesAsync();
            }
        }

        await EnsureMembershipAsync(request.RequestedByUserId, organizationId, grantedRole, now);

        // Admitting somebody as admin hands them the family, and its plan, in the same act — a
        // family has exactly one admin, so the approver necessarily stops being it. Done here
        // rather than left to a follow-up call, because a moment with two admins is a moment where
        // "the admin pays" names nobody.
        if (grantedRole == UserRole.Admin)
        {
            // A guest admitted as admin has no home family — that is what being a guest meant —
            // and User.OrganizationId is what UserContextMiddleware serves as the caller's
            // organization. Left null they would own the family and its billing while every
            // organization-scoped read showed them nothing. Somebody who already has one keeps it.
            var admitted = await _unitOfWork.Users.GetByIdAsync(request.RequestedByUserId);
            if (admitted is not null && admitted.OrganizationId is null)
            {
                admitted.OrganizationId = organizationId;
                admitted.UpdatedDate = now;
                _unitOfWork.Users.Update(admitted);
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
        await _unitOfWork.CommitTransactionAsync();
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

    /// <summary>
    /// Opens a transaction and holds the family in it, so a read and the write it justifies cannot
    /// be split by another caller. Rolls back unless the caller commits.
    /// </summary>
    private async Task<GuardedFamily> BeginGuardedAsync(Guid organizationId, CancellationToken ct)
    {
        await _unitOfWork.BeginTransactionAsync();

        if (!await _guard.HoldAsync(organizationId, ct))
        {
            await _unitOfWork.RollbackTransactionAsync();
            throw new KeyNotFoundException("Request not found");
        }

        return new GuardedFamily(_unitOfWork);
    }

    private sealed class GuardedFamily(IUnitOfWork unitOfWork) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await unitOfWork.RollbackTransactionAsync();
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
