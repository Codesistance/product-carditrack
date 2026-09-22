using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Security;
using CardiTrack.Infrastructure.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CardiTrack.Infrastructure.Services;

/// <summary>
/// Issues and redeems caregiver invitations.
/// </summary>
/// <remarks>
/// <para>
/// Built on the same parts as <see cref="DeviceConnectionInviteService"/> — the same token helper,
/// the same "hash only, returned once" rule, the same expiry-is-a-timestamp reading — because the
/// wearer invitation already argued those through and a second, subtly different answer to the
/// same question is how the two drift apart.
/// </para>
/// <para>
/// What it does not share is the ending. A wearer's invitation resolves anonymously; this one
/// resolves for an account, so <see cref="RedeemAsync"/> demands an authenticated user and the
/// token buys nothing but the right to have a membership and a grant written for that user.
/// </para>
/// </remarks>
public class CaregiverInviteService : ICaregiverInviteService
{
    private const string DeniedMessage = "CardiMember not found";
    private const string InvitesRoute = "/api/v1/cardimembers/{cardiMemberId}/caregiver-invites";
    private const string RedeemRoute = "/api/v1/caregiver-invites/redeem";

    /// <summary>Statuses an invitation can still be acted on from.</summary>
    private static readonly CaregiverInviteStatus[] LiveStatuses =
        [CaregiverInviteStatus.Pending, CaregiverInviteStatus.Opened];

    private readonly IUnitOfWork _unitOfWork;
    private readonly IAuditLogRepository _auditLogs;
    private readonly IOptions<CaregiverInviteOptions> _options;
    private readonly ILogger<CaregiverInviteService> _logger;
    private readonly TimeProvider _timeProvider;

    public CaregiverInviteService(
        IUnitOfWork unitOfWork,
        IAuditLogRepository auditLogs,
        IOptions<CaregiverInviteOptions> options,
        ILogger<CaregiverInviteService> logger,
        TimeProvider? timeProvider = null)
    {
        _unitOfWork = unitOfWork;
        _auditLogs = auditLogs;
        _options = options;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<CaregiverInviteResponse> CreateAsync(
        Guid requestingUserId,
        Guid cardiMemberId,
        CreateCaregiverInviteRequest request,
        string requestBaseUrl,
        CancellationToken ct = default)
    {
        var member = await RequireAdminOfMembersFamilyAsync(requestingUserId, cardiMemberId);
        var role = ResolveRole(request.Role);
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        var token = InviteTokens.Mint();
        var invite = new CaregiverInvite
        {
            CardiMemberId = cardiMemberId,
            OrganizationId = member.OrganizationId,
            CreatedByUserId = requestingUserId,
            Role = role,
            CanViewHealthData = request.CanViewHealthData,
            ReceiveAlerts = request.ReceiveAlerts,
            TokenHash = InviteTokens.HashOrNull(token)!,
            Status = CaregiverInviteStatus.Pending,
            ExpiresAt = now.AddDays(_options.Value.LifetimeDays),
        };

        await _unitOfWork.CaregiverInvites.AddAsync(invite);
        await _unitOfWork.SaveChangesAsync();

        _logger.LogInformation(
            "Caregiver invite {InviteId} created for member {CardiMemberId} as {Role}.",
            invite.Id, cardiMemberId, role);

        await AuditAsync(invite, "CreateCaregiverInvite", InvitesRoute, "POST", 201, ct);

        var response = ToResponse(invite, now);
        response.Url = BuildInviteUrl(requestBaseUrl, token);
        return response;
    }

    public async Task<IReadOnlyList<CaregiverInviteResponse>> ListAsync(
        Guid requestingUserId, Guid cardiMemberId, CancellationToken ct = default)
    {
        await RequireAdminOfMembersFamilyAsync(requestingUserId, cardiMemberId);

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var invites = await _unitOfWork.CaregiverInvites.GetForMemberAsync(cardiMemberId, ct);

        // Url stays null here by construction — ToResponse never sets it.
        return [.. invites.Select(i => ToResponse(i, now))];
    }

    public async Task<CaregiverInviteResponse> RevokeAsync(
        Guid requestingUserId, Guid cardiMemberId, Guid inviteId, CancellationToken ct = default)
    {
        await RequireAdminOfMembersFamilyAsync(requestingUserId, cardiMemberId);

        var invite = await _unitOfWork.CaregiverInvites.GetByIdAsync(inviteId);
        if (invite is null || invite.CardiMemberId != cardiMemberId)
            throw new KeyNotFoundException("Invitation not found");

        var now = _timeProvider.GetUtcNow().UtcDateTime;

        // Idempotent: an invitation already terminal simply comes back as it is. Revoking twice is
        // what a double tap looks like, and it is not a failure.
        if (await _unitOfWork.CaregiverInvites.TryResolveAsync(
                inviteId, LiveStatuses, CaregiverInviteStatus.Revoked, now, null, ct))
        {
            invite.Status = CaregiverInviteStatus.Revoked;
            invite.ResolvedAt = now;
            await AuditAsync(invite, "RevokeCaregiverInvite", InvitesRoute, "DELETE", 200, ct);
        }

        return ToResponse(invite, now);
    }

    public async Task<CaregiverInviteView?> ViewAsync(string token, CancellationToken ct = default)
    {
        var invite = await FindLiveAsync(token, ct);
        if (invite is null)
            return null;

        var member = await _unitOfWork.CardiMembers.GetByIdAsync(invite.CardiMemberId);
        var inviter = await _unitOfWork.Users.GetByIdAsync(invite.CreatedByUserId);
        if (member is null || !member.IsActive || inviter is null)
            return null;

        // Opening is only a fact about the invitation, so a failure to record it must not stop the
        // page rendering — the invitee is holding a live link either way.
        await _unitOfWork.CaregiverInvites.TryMarkOpenedAsync(
            invite.Id, _timeProvider.GetUtcNow().UtcDateTime, ct);

        return new CaregiverInviteView(
            FirstName(member.Name),
            FirstName(inviter.Name),
            invite.ExpiresAt);
    }

    public async Task<CaregiverInviteRedemption> RedeemAsync(
        string token, Guid redeemingUserId, CancellationToken ct = default)
    {
        if (redeemingUserId == Guid.Empty)
            throw new KeyNotFoundException("Invitation not found");

        var invite = await FindLiveAsync(token, ct)
            ?? throw new KeyNotFoundException("Invitation not found");

        // An invitation must not outlive the authority that issued it. The issuer may have been
        // demoted, removed from the family, or had their own account closed since they sent it.
        if (!await IsAdminOfAsync(invite.CreatedByUserId, invite.OrganizationId))
        {
            _logger.LogWarning(
                "Caregiver invite {InviteId} refused: issuer {IssuerId} is no longer an admin of {OrganizationId}.",
                invite.Id, invite.CreatedByUserId, invite.OrganizationId);
            throw new KeyNotFoundException("Invitation not found");
        }

        // The member may have been erased since the invitation was written. The cascade deletes
        // invitations now, but a redemption already in flight can arrive after that sweep passed
        // this table — the schema has no foreign key to stop the grant landing anyway, the same
        // gap IMemberWriteGuard exists for. Checked here as well as swept there, because a link
        // to a member nobody can name is unreachable and unremovable from the app.
        var member = await _unitOfWork.CardiMembers.GetByIdAsync(invite.CardiMemberId);
        if (member is not { IsActive: true })
        {
            _logger.LogWarning(
                "Caregiver invite {InviteId} refused: CardiMember {CardiMemberId} is no longer active.",
                invite.Id, invite.CardiMemberId);
            throw new KeyNotFoundException("Invitation not found");
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;

        // Claim the invitation before writing anything. Losing this race means somebody else
        // already spent it, or an admin revoked it a moment ago — either way there is nothing to
        // grant, and grants written first would have to be unwound.
        if (!await _unitOfWork.CaregiverInvites.TryResolveAsync(
                invite.Id, LiveStatuses, CaregiverInviteStatus.Accepted, now, redeemingUserId, ct))
        {
            throw new KeyNotFoundException("Invitation not found");
        }

        // Checked after the claim: a redemption that cannot be honoured should spend the
        // invitation rather than leave it live for somebody to try again into a full family.
        // Someone already in the family is exempt — they take up no new place.
        var rejoining = await _unitOfWork.UserOrganizations.GetAsync(redeemingUserId, invite.OrganizationId);
        if (rejoining is not { IsActive: true })
            await PlanLimits.RequireRoomForAnotherPersonAsync(_unitOfWork, invite.OrganizationId, ct);

        var existingLinks = await _unitOfWork.UserCardiMembers.GetByUserIdAsync(redeemingUserId);
        var existingLink = existingLinks.FirstOrDefault(
            l => l.CardiMemberId == invite.CardiMemberId && l.IsActive);
        var alreadyHadAccess = existingLink is not null;

        if (!alreadyHadAccess)
        {
            await _unitOfWork.UserCardiMembers.AddAsync(new UserCardiMember
            {
                UserId = redeemingUserId,
                CardiMemberId = invite.CardiMemberId,
                RelationshipType = RelationshipType.Other,
                IsPrimaryCaregiver = false,
                CanViewHealthData = invite.CanViewHealthData,
                ReceiveAlerts = invite.ReceiveAlerts,
            });
        }

        await EnsureMembershipAsync(redeemingUserId, invite.OrganizationId, invite.Role, now);
        await _unitOfWork.SaveChangesAsync();

        invite.Status = CaregiverInviteStatus.Accepted;
        invite.ResolvedAt = now;
        invite.AcceptedByUserId = redeemingUserId;
        await AuditRedemptionAsync(invite, redeemingUserId, ct);

        _logger.LogInformation(
            "Caregiver invite {InviteId} redeemed by {UserId}; already had access: {AlreadyHadAccess}.",
            invite.Id, redeemingUserId, alreadyHadAccess);

        return new CaregiverInviteRedemption(
            invite.CardiMemberId,
            invite.OrganizationId,
            ToWireRole(invite.Role),
            existingLink?.CanViewHealthData ?? invite.CanViewHealthData,
            existingLink?.ReceiveAlerts ?? invite.ReceiveAlerts,
            alreadyHadAccess);
    }

    public async Task<bool> DeclineAsync(string token, CancellationToken ct = default)
    {
        var invite = await FindLiveAsync(token, ct);
        if (invite is null)
            return false;

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var declined = await _unitOfWork.CaregiverInvites.TryResolveAsync(
            invite.Id, LiveStatuses, CaregiverInviteStatus.Declined, now, null, ct);

        if (declined)
        {
            invite.Status = CaregiverInviteStatus.Declined;
            invite.ResolvedAt = now;
            await AuditAsync(invite, "DeclineCaregiverInvite", RedeemRoute, "POST", 200, ct);
        }

        return declined;
    }

    /// <summary>
    /// Adds the membership, or reactivates one the person left behind. Reactivating rather than
    /// inserting is what the unique index on (UserId, OrganizationId) requires, and it keeps a
    /// family's history of a person to one row.
    /// </summary>
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
    /// The member, once the caller is confirmed to be an active Admin of the family that owns it.
    /// Refusals use the same message a missing member gets, so an outsider cannot tell "not yours"
    /// from "no such member" by probing.
    /// </summary>
    private async Task<CardiMember> RequireAdminOfMembersFamilyAsync(Guid requestingUserId, Guid cardiMemberId)
    {
        if (requestingUserId == Guid.Empty)
            throw new KeyNotFoundException(DeniedMessage);

        var member = await _unitOfWork.CardiMembers.GetByIdAsync(cardiMemberId);
        if (member is null || !member.IsActive)
            throw new KeyNotFoundException(DeniedMessage);

        if (!await IsAdminOfAsync(requestingUserId, member.OrganizationId))
            throw new KeyNotFoundException(DeniedMessage);

        return member;
    }

    private async Task<bool> IsAdminOfAsync(Guid userId, Guid organizationId)
    {
        var membership = await _unitOfWork.UserOrganizations.GetAsync(userId, organizationId);
        return membership is { IsActive: true, Role: UserRole.Admin };
    }

    /// <summary>
    /// The invitation a token names, if it is still acceptable. Null covers every other case —
    /// unknown token, expired, already spent, withdrawn — because none of them should be
    /// distinguishable to somebody holding a guess.
    /// </summary>
    private async Task<CaregiverInvite?> FindLiveAsync(string token, CancellationToken ct)
    {
        var hash = InviteTokens.HashOrNull(token);
        if (hash is null)
            return null;

        var invite = await _unitOfWork.CaregiverInvites.GetByTokenHashAsync(hash, ct);
        if (invite is null)
            return null;

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        return LiveStatuses.Contains(invite.Status) && invite.ExpiresAt > now ? invite : null;
    }

    private static UserRole ResolveRole(string? role) => role?.Trim().ToLowerInvariant() switch
    {
        "admin" => UserRole.Admin,
        // Staff is an Enterprise concern and is never assignable to a family.
        _ => UserRole.Member,
    };

    private static string ToWireRole(UserRole role) =>
        role == UserRole.Admin ? "admin" : "member";

    /// <summary>The member's name as the landing page says it — first word only.</summary>
    private static string FirstName(string name)
    {
        var trimmed = name.Trim();
        var space = trimmed.IndexOf(' ');
        return space < 0 ? trimmed : trimmed[..space];
    }

    private string BuildInviteUrl(string requestBaseUrl, string token)
    {
        var configured = _options.Value.PublicBaseUrl;
        var baseUrl = (string.IsNullOrWhiteSpace(configured) ? requestBaseUrl : configured).TrimEnd('/');

        return $"{baseUrl}/join?t={Uri.EscapeDataString(token)}";
    }

    private CaregiverInviteResponse ToResponse(CaregiverInvite invite, DateTime utcNow) => new()
    {
        InviteId = invite.Id,
        CardiMemberId = invite.CardiMemberId,
        Role = ToWireRole(invite.Role),
        CanViewHealthData = invite.CanViewHealthData,
        ReceiveAlerts = invite.ReceiveAlerts,
        // Expiry is stored as an instant and reported as a status, so the list does not have to
        // re-derive it from a device clock that may not agree with ours.
        Status = LiveStatuses.Contains(invite.Status) && invite.ExpiresAt <= utcNow
            ? "expired"
            : invite.Status.ToString().ToLowerInvariant(),
        ExpiresAt = invite.ExpiresAt,
        OpenedAt = invite.OpenedAt,
        ResolvedAt = invite.ResolvedAt,
        AcceptedByUserId = invite.AcceptedByUserId,
    };

    private Task AuditRedemptionAsync(CaregiverInvite invite, Guid redeemingUserId, CancellationToken ct) =>
        AppendAuditAsync(invite, redeemingUserId, "RedeemCaregiverInvite", RedeemRoute, "POST", 200, ct);

    private Task AuditAsync(
        CaregiverInvite invite,
        string action,
        string requestPath,
        string httpMethod,
        int responseStatus,
        CancellationToken ct) =>
        AppendAuditAsync(invite, invite.CreatedByUserId, action, requestPath, httpMethod, responseStatus, ct);

    private async Task AppendAuditAsync(
        CaregiverInvite invite,
        Guid userId,
        string action,
        string requestPath,
        string httpMethod,
        int responseStatus,
        CancellationToken ct)
    {
        try
        {
            await _auditLogs.AppendAsync(new AuditLog
            {
                UserId = userId,
                CardiMemberId = invite.CardiMemberId,
                Action = action,
                EntityType = "CaregiverInvite",
                EntityId = invite.Id,
                Timestamp = _timeProvider.GetUtcNow().UtcDateTime,
                RequestPath = requestPath,
                HttpMethod = httpMethod,
                IpAddress = string.Empty,
                UserAgent = string.Empty,
                ResponseStatus = responseStatus,
            }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to audit {Action} for caregiver invite {InviteId}.", action, invite.Id);
        }
    }
}
