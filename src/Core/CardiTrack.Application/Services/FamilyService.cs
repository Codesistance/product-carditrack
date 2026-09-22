using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Exceptions;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Services;

/// <inheritdoc cref="IFamilyService"/>
public class FamilyService : IFamilyService
{
    /// <summary>
    /// Deliberately identical to the message a family that does not exist gets, so somebody
    /// outside cannot tell "not yours" from "no such family" by probing — the same choice
    /// <see cref="CardiMemberAccessService"/> makes for members.
    /// </summary>
    internal const string DeniedMessage = "Family not found";

    private readonly IUnitOfWork _unitOfWork;

    public FamilyService(IUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    public async Task<IReadOnlyList<FamilySummary>> GetMineAsync(
        Guid requestingUserId, CancellationToken ct = default)
    {
        if (requestingUserId == Guid.Empty)
            return [];

        var user = await _unitOfWork.Users.GetByIdAsync(requestingUserId);
        var memberships = (await _unitOfWork.UserOrganizations.GetByUserIdAsync(requestingUserId))
            .Where(m => m.IsActive)
            .ToList();
        if (memberships.Count == 0)
            return [];

        // One pass over the caller's grants rather than one query per family: a caregiver watches a
        // handful of people, and the names below have to come from that set anyway.
        var links = (await _unitOfWork.UserCardiMembers.GetByUserIdAsync(requestingUserId))
            .Where(l => l.IsActive && l.CanViewHealthData)
            .Select(l => l.CardiMemberId)
            .ToHashSet();

        var summaries = new List<FamilySummary>(memberships.Count);
        foreach (var membership in memberships)
        {
            var organization = await _unitOfWork.Organizations.GetByIdAsync(membership.OrganizationId);
            if (organization is null || !organization.IsActive)
                continue;

            var people = (await _unitOfWork.UserOrganizations.GetByOrganizationIdAsync(membership.OrganizationId))
                .Count(m => m.IsActive);

            // Only the members this caller was granted — the list is what they can open, not an
            // inventory of who the family watches.
            var watched = (await _unitOfWork.CardiMembers.GetByOrganizationIdAsync(membership.OrganizationId))
                .Where(m => links.Contains(m.Id))
                .Select(m => m.Name)
                .ToList();

            summaries.Add(new FamilySummary(
                organization.Id,
                organization.Name,
                ToWireRole(membership.Role),
                IsHomeFamily: user?.OrganizationId == organization.Id,
                people,
                watched));
        }

        return summaries;
    }

    public async Task<IReadOnlyList<FamilyMemberSummary>> GetMembersAsync(
        Guid requestingUserId, Guid organizationId, CancellationToken ct = default)
    {
        await RequireMembershipAsync(requestingUserId, organizationId);
        return await BuildRosterAsync(requestingUserId, organizationId);
    }

    public async Task<IReadOnlyList<FamilyMemberSummary>> TransferAdminAsync(
        Guid requestingUserId, Guid organizationId, Guid newAdminUserId, CancellationToken ct = default)
    {
        var current = await RequireAdminAsync(requestingUserId, organizationId);

        if (newAdminUserId == requestingUserId)
        {
            // Not an error worth a code: they are already the admin and nothing needs to happen.
            return await BuildRosterAsync(requestingUserId, organizationId);
        }

        var successor = await _unitOfWork.UserOrganizations.GetAsync(newAdminUserId, organizationId);
        if (successor is null || !successor.IsActive)
            throw new KeyNotFoundException("That person isn't in this family.");

        var now = DateTime.UtcNow;

        // Both sides of the handover in one save. Promotion and demotion are the same act here —
        // a family has one admin, and that admin pays for it, so a moment with two or none is not
        // a state the product has an answer for.
        successor.Role = UserRole.Admin;
        successor.UpdatedDate = now;
        _unitOfWork.UserOrganizations.Update(successor);

        current.Role = UserRole.Member;
        current.UpdatedDate = now;
        _unitOfWork.UserOrganizations.Update(current);

        await _unitOfWork.SaveChangesAsync();

        return await BuildRosterAsync(requestingUserId, organizationId);
    }

    public async Task RemoveMemberAsync(
        Guid requestingUserId, Guid organizationId, Guid userId, CancellationToken ct = default)
    {
        await RequireAdminAsync(requestingUserId, organizationId);

        if (userId == requestingUserId)
        {
            throw new FamilyRuleException(
                FamilyRuleException.CannotDemoteLastAdmin,
                "You're this family's admin. Hand it to someone else before you go.");
        }

        var membership = await _unitOfWork.UserOrganizations.GetAsync(userId, organizationId);
        if (membership is null || !membership.IsActive)
            throw new KeyNotFoundException("That person isn't in this family.");

        await DeactivateAsync(membership, organizationId, userId);
    }

    public async Task LeaveAsync(Guid requestingUserId, Guid organizationId, CancellationToken ct = default)
    {
        var membership = await RequireMembershipAsync(requestingUserId, organizationId);

        if (membership.Role == UserRole.Admin)
        {
            var others = (await _unitOfWork.UserOrganizations.GetByOrganizationIdAsync(organizationId))
                .Count(m => m.IsActive && m.UserId != requestingUserId);

            // Alone in a family they run, "leaving" is not a family operation at all — there is no
            // family left afterwards. Saying so sends them to the flow that actually erases things,
            // rather than deactivating a row and leaving an orphaned family behind.
            throw others == 0
                ? new FamilyRuleException(
                    FamilyRuleException.UseAccountDeletion,
                    "You're the only person here. To finish with CardiTrack, delete your account instead.")
                : new FamilyRuleException(
                    FamilyRuleException.AdminMustTransferFirst,
                    "You're this family's admin. Hand it to someone else before you go.");
        }

        await DeactivateAsync(membership, organizationId, requestingUserId);
    }

    /// <summary>
    /// Ends a membership and the grants that came with it. The grants go because they were the
    /// point of being in the family: leaving a family but keeping a live view of its members would
    /// be a departure in name only.
    /// </summary>
    private async Task DeactivateAsync(UserOrganization membership, Guid organizationId, Guid userId)
    {
        var now = DateTime.UtcNow;

        membership.IsActive = false;
        membership.UpdatedDate = now;
        _unitOfWork.UserOrganizations.Update(membership);

        var familyMemberIds = (await _unitOfWork.CardiMembers.GetByOrganizationIdAsync(organizationId))
            .Select(m => m.Id)
            .ToHashSet();

        var links = await _unitOfWork.UserCardiMembers.GetByUserIdAsync(userId);
        foreach (var link in links.Where(l => l.IsActive && familyMemberIds.Contains(l.CardiMemberId)))
        {
            link.IsActive = false;
            link.UpdatedDate = now;
            _unitOfWork.UserCardiMembers.Update(link);
        }

        await _unitOfWork.SaveChangesAsync();
    }

    private async Task<IReadOnlyList<FamilyMemberSummary>> BuildRosterAsync(
        Guid requestingUserId, Guid organizationId)
    {
        var memberships = (await _unitOfWork.UserOrganizations.GetByOrganizationIdAsync(organizationId))
            .Where(m => m.IsActive)
            .ToList();

        var roster = new List<FamilyMemberSummary>(memberships.Count);
        foreach (var membership in memberships)
        {
            var user = await _unitOfWork.Users.GetByIdAsync(membership.UserId);
            if (user is null || !user.IsActive)
                continue;

            roster.Add(new FamilyMemberSummary(
                user.Id,
                user.Name,
                user.Email,
                ToWireRole(membership.Role),
                IsYou: user.Id == requestingUserId,
                membership.JoinedDate));
        }

        // Admin first, then by how long they have been here: the roster's first question is always
        // "who runs this", and the answer should not move as people join.
        return [.. roster
            .OrderByDescending(r => r.Role == "admin")
            .ThenBy(r => r.JoinedDate)];
    }

    private async Task<UserOrganization> RequireMembershipAsync(Guid requestingUserId, Guid organizationId)
    {
        if (requestingUserId == Guid.Empty)
            throw new KeyNotFoundException(DeniedMessage);

        var membership = await _unitOfWork.UserOrganizations.GetAsync(requestingUserId, organizationId);
        if (membership is null || !membership.IsActive)
            throw new KeyNotFoundException(DeniedMessage);

        return membership;
    }

    private async Task<UserOrganization> RequireAdminAsync(Guid requestingUserId, Guid organizationId)
    {
        var membership = await RequireMembershipAsync(requestingUserId, organizationId);
        if (membership.Role != UserRole.Admin)
            throw new KeyNotFoundException(DeniedMessage);

        return membership;
    }

    private static string ToWireRole(UserRole role) => role == UserRole.Admin ? "admin" : "member";
}
