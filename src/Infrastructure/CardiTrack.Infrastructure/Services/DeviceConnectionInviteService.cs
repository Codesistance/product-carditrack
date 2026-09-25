using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Exceptions;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Domain.Extensions;
using CardiTrack.Infrastructure.Security;
using CardiTrack.Infrastructure.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CardiTrack.Infrastructure.Services;

/// <summary>
/// Wearer-side device onboarding: the caregiver mints an invitation, hands it over as a link or a
/// QR code, and the wearer authorizes their own wearable from their own device.
/// </summary>
/// <remarks>
/// <para>
/// The flow it replaces required the wearer to be holding the caregiver's phone, because the OAuth
/// round trip ran inside the app. This one moves the consent to where the wearer already is, which
/// is both the usability fix and — the part that matters more — the first time a wearer has given
/// consent on a screen of their own. See <c>docs/compliance/dpia.md</c> R-A7.
/// </para>
/// <para>
/// <strong>Authorization is the same tier as connecting a device in person</strong> — an active
/// <c>UserCardiMember</c> link, checked on every call. Not the stricter manage tier, deliberately:
/// a caregiver who holds a link can already run the whole connection on their own phone, so
/// requiring more to do it by invitation would guard nothing while blocking the case the feature
/// exists for. What the stricter tier does still guard is the other direction — disconnecting,
/// which is unchanged.
/// </para>
/// <para>
/// <strong>The anonymous half is deliberately anaemic.</strong> Everything the token can reach is
/// scoped to the one invitation it names. There is no member id, user id or provider on any of
/// those signatures for a caller to substitute, and the only facts they return are the four on
/// <see cref="WearerInviteView"/>. Every failure — unknown token, expired, spent, declined, revoked
/// — is reported the same way, so the endpoints cannot be used to learn which of those a token is.
/// </para>
/// </remarks>
public class DeviceConnectionInviteService : IDeviceConnectionInviteService
{
    /// <summary>
    /// Wire values for <see cref="DeviceInviteChannel"/>. Short and stable: they travel in request
    /// bodies and are rendered by a mobile build that ships on its own schedule, so they are not
    /// derived from the enum's names — renaming a C# member should not break a shipped app.
    /// </summary>
    private const string LinkChannel = "link";
    private const string QrChannel = "qr";

    /// <summary>
    /// Routes recorded on the audit entries this service writes. Constants rather than values read
    /// from a request, because half these events arrive on the anonymous wearer endpoints where the
    /// service is the only thing that knows which action it is performing — and because an audit
    /// trail should say which operation happened, not which URL spelling reached it.
    /// </summary>
    private const string InvitesRoute = "/api/v1/cardimembers/{cardiMemberId}/device-invites";
    private const string ConnectStartRoute = "/connect/start";
    private const string ConnectDeclineRoute = "/connect/decline";

    /// <summary>
    /// Where a completion actually arrives: the provider's bounce, not our own consent page. The
    /// wearer's grant comes back from the provider to <c>oauth/redirect/{provider}</c>, and filing
    /// it under <c>/connect</c> would point an investigation at the wrong request entirely.
    /// </summary>
    private static string BounceRoute(string provider) => $"/api/v1/oauth/redirect/{provider}";

    private readonly IUnitOfWork _unitOfWork;
    private readonly IDeviceConnectionService _connections;
    private readonly IAuditLogRepository _auditLogs;
    private readonly IOptions<DeviceInviteOptions> _options;
    private readonly ILogger<DeviceConnectionInviteService> _logger;
    private readonly TimeProvider _timeProvider;

    public DeviceConnectionInviteService(
        IUnitOfWork unitOfWork,
        IDeviceConnectionService connections,
        IAuditLogRepository auditLogs,
        IOptions<DeviceInviteOptions> options,
        ILogger<DeviceConnectionInviteService> logger,
        TimeProvider? timeProvider = null)
    {
        _unitOfWork = unitOfWork;
        _connections = connections;
        _auditLogs = auditLogs;
        _options = options;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<DeviceInviteResponse> CreateAsync(
        Guid requestingUserId,
        Guid cardiMemberId,
        CreateDeviceInviteRequest request,
        string requestBaseUrl,
        CancellationToken ct = default)
    {
        await EnsureMemberAccessAsync(requestingUserId, cardiMemberId);

        if (!DeviceProviderNames.TryResolve(request.Provider, out var deviceType))
        {
            throw new DeviceConnectionException(
                DeviceConnectionException.UnsupportedProvider,
                $"'{request.Provider}' is not a supported server-OAuth provider.");
        }

        // Checked now rather than when the wearer comes back: a caregiver who may not remove this
        // device, or a device that is already gone, should be told before a link goes out, not
        // leave a wearer to grant consent for a replacement that is then refused.
        if (request.ReplacesDeviceId is { } replacesDeviceId)
            await _connections.EnsureCanReplaceAsync(requestingUserId, cardiMemberId, replacesDeviceId, ct);

        var channel = ResolveChannel(request.Channel);
        var options = _options.Value;
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        // Asking for a new invitation is how a caregiver takes back an old one — they sent it to
        // the wrong number, or it has gone stale and they would rather it stopped working than sat
        // in somebody's inbox for the rest of the day.
        var superseded = await _unitOfWork.DeviceConnectionInvites.RevokeLiveAsync(
            cardiMemberId, deviceType, now, ct);

        var token = InviteTokens.Mint();
        var invite = new DeviceConnectionInvite
        {
            CardiMemberId = cardiMemberId,
            CreatedByUserId = requestingUserId,
            DeviceType = deviceType,
            Channel = channel,
            TokenHash = InviteTokens.HashOrNull(token)!,
            Status = DeviceInviteStatus.Pending,
            ExpiresAt = now.AddMinutes(channel == DeviceInviteChannel.QrCode
                ? options.QrLifetimeMinutes
                : options.LinkLifetimeMinutes),
            ReplacesDeviceConnectionId = request.ReplacesDeviceId,
        };

        await _unitOfWork.DeviceConnectionInvites.AddAsync(invite);
        await _unitOfWork.SaveChangesAsync();

        _logger.LogInformation(
            "Device invite {InviteId} created for member {CardiMemberId} on {DeviceType} via {Channel}; " +
            "{Superseded} earlier invite(s) revoked.",
            invite.Id, cardiMemberId, deviceType, channel, superseded);

        await AuditAsync(invite, "CreateDeviceInvite", InvitesRoute, "POST", StatusCodes201, ct);

        var response = ToResponse(invite, now);
        response.Url = BuildInviteUrl(requestBaseUrl, token);
        return response;
    }

    public async Task<DeviceInviteResponse> GetAsync(
        Guid requestingUserId, Guid cardiMemberId, Guid inviteId, CancellationToken ct = default)
    {
        await EnsureMemberAccessAsync(requestingUserId, cardiMemberId);

        var invite = await RequireInviteAsync(cardiMemberId, inviteId);
        return ToResponse(invite, _timeProvider.GetUtcNow().UtcDateTime);
    }

    public async Task<DeviceInviteResponse> RevokeAsync(
        Guid requestingUserId, Guid cardiMemberId, Guid inviteId, CancellationToken ct = default)
    {
        await EnsureMemberAccessAsync(requestingUserId, cardiMemberId);

        var invite = await RequireInviteAsync(cardiMemberId, inviteId);
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        // A cancel that loses to a completion a second earlier is not an error to report. The
        // caregiver's intent — "stop this from being usable" — is satisfied either way, and telling
        // them the cancel failed would send them looking for a problem that is not there. What they
        // get back is the invite's real outcome, so the screen can say the connection landed.
        var revoked = await _unitOfWork.DeviceConnectionInvites.TryResolveAsync(
            inviteId, LiveStatuses, DeviceInviteStatus.Revoked, now, deviceConnectionId: null, ct);

        if (revoked)
        {
            _logger.LogInformation("Device invite {InviteId} revoked by its caregiver.", inviteId);
            await AuditAsync(invite, "RevokeDeviceInvite", InvitesRoute, "DELETE", StatusCodes200, ct);
        }

        return ToResponse(await RequireInviteAsync(cardiMemberId, inviteId), now);
    }

    public async Task<WearerInviteView?> ViewAsync(string token, CancellationToken ct = default)
    {
        var invite = await ResolveLiveInviteAsync(token, ct);
        if (invite is null)
            return null;

        var member = await _unitOfWork.CardiMembers.GetByIdAsync(invite.CardiMemberId);
        var caregiver = await _unitOfWork.Users.GetByIdAsync(invite.CreatedByUserId);

        // A member who has since been deactivated, or a caregiver whose account has gone, leaves an
        // invitation that names nobody. Refusing here rather than rendering blanks: a consent screen
        // that cannot say who is asking on whose behalf is not something anyone should agree to.
        if (member is null || !member.IsActive || caregiver is null)
            return null;

        return new WearerInviteView(
            MemberFirstName: member.FirstName,
            CaregiverFirstName: NamePlaceholder.FirstName(caregiver.Name) ?? caregiver.Name,
            DeviceDisplayName: invite.DeviceType.GetDisplayName(),
            Provider: DeviceProviderNames.ToWireName(invite.DeviceType),
            ExpiresAt: invite.ExpiresAt);
    }

    public async Task<string?> StartAsync(string token, CancellationToken ct = default)
    {
        var invite = await ResolveLiveInviteAsync(token, ct);
        if (invite is null)
            return null;

        var now = _timeProvider.GetUtcNow().UtcDateTime;

        // Marked opened before the redirect, not after the grant: "they have seen it" is what the
        // caregiver's waiting screen needs, and a wearer who opens the consent page and thinks
        // better of it has still seen it.
        if (await _unitOfWork.DeviceConnectionInvites.TryMarkOpenedAsync(invite.Id, now, ct))
            await AuditAsync(invite, "OpenDeviceInvite", ConnectStartRoute, "POST", StatusCodes200, ct);

        return await _connections.InitiateWearerConnectionAsync(
            invite.Id, invite.CreatedByUserId, invite.CardiMemberId, invite.DeviceType,
            invite.ReplacesDeviceConnectionId, ct);
    }

    public async Task<bool> DeclineAsync(string token, CancellationToken ct = default)
    {
        var invite = await ResolveLiveInviteAsync(token, ct);
        if (invite is null)
            return false;

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var declined = await _unitOfWork.DeviceConnectionInvites.TryResolveAsync(
            invite.Id, LiveStatuses, DeviceInviteStatus.Declined, now, deviceConnectionId: null, ct);

        if (declined)
        {
            _logger.LogInformation("Device invite {InviteId} declined by the wearer.", invite.Id);
            await AuditAsync(invite, "DeclineDeviceInvite", ConnectDeclineRoute, "POST", StatusCodes200, ct);
        }

        return declined;
    }

    public async Task<WearerConnectionOutcome> CompleteFromCallbackAsync(
        string provider, string state, string? code, string? error, CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code))
        {
            // The wearer said no at the provider, or the provider refused. Their invitation stays
            // live so they can change their mind from the page they land on — sending them back to
            // the caregiver for a fresh link over a mis-tap would be a poor way to treat the one
            // person in this flow who never asked to be in it.
            _logger.LogInformation(
                "Wearer device authorization for {Provider} was not granted: {Error}",
                provider, string.IsNullOrEmpty(error) ? "no_code" : error);

            return new WearerConnectionOutcome(WearerConnectionResult.Denied, null, CanRetry: true);
        }

        // Checked before the exchange, not after it. The OAuth state knows nothing about the
        // invitation row and lives for fifteen minutes, so a caregiver who cancels while the wearer
        // is still on the provider's consent screen would otherwise be overruled by the wearer
        // finishing: the grant would be stored and only the invitation's own status transition would
        // fail, silently. The caregiver's withdrawal has to win, and the only place it can is here,
        // before anything is stored.
        var inviteId = await _connections.PeekWearerInviteIdAsync(provider, state, ct);
        if (inviteId is null || !await IsStillLiveAsync(inviteId.Value))
        {
            _logger.LogInformation(
                "Wearer device connection refused — the invitation is no longer live.");

            // Nothing left to retry with, so the page must not offer one.
            return new WearerConnectionOutcome(WearerConnectionResult.Failed, null, CanRetry: false);
        }

        WearerConnectionCompletion completion;
        try
        {
            completion = await _connections.CompleteWearerConnectionAsync(provider, state, code, ct);
        }
        catch (DeviceConnectionException ex)
        {
            _logger.LogWarning(ex,
                "Wearer device connection failed with code {Code}.", ex.Code);

            // A spent state and a dead invitation are the two failures a retry cannot fix — there is
            // nothing left to retry with, and a button would only lead to a dead end. Everything
            // else (the provider rejecting the exchange, a transient fault) is worth another go
            // while the invitation is still live.
            var canRetry = ex.Code is not (DeviceConnectionException.InvalidStateToken
                                           or DeviceConnectionException.InviteNotLive);
            return new WearerConnectionOutcome(WearerConnectionResult.Failed, null, canRetry);
        }
        catch (KeyNotFoundException ex)
        {
            // The caregiver's access was withdrawn, or the member deactivated, between the
            // invitation and this moment. Nothing was stored; say no more than that.
            _logger.LogWarning(ex,
                "Wearer device connection refused — the invitation's caregiver no longer has access.");
            return new WearerConnectionOutcome(WearerConnectionResult.Failed, null, CanRetry: false);
        }

        // The invitation was already moved to Completed, inside the same transaction as the
        // connection — see ExchangeAndStoreAsync's claim. Nothing to transition here; this is only
        // the record of what happened.
        var invite = await _unitOfWork.DeviceConnectionInvites.GetByIdAsync(completion.InviteId);
        if (invite is not null)
        {
            await AuditAsync(
                invite, "CompleteDeviceInvite", BounceRoute(provider), "GET", StatusCodes200, ct);
        }

        _logger.LogInformation(
            "Device invite {InviteId} completed; connection {DeviceId} stored.",
            completion.InviteId, completion.Device.DeviceId);

        return new WearerConnectionOutcome(
            WearerConnectionResult.Completed, completion.Device.DisplayName, CanRetry: false);
    }

    private static readonly DeviceInviteStatus[] LiveStatuses =
        [DeviceInviteStatus.Pending, DeviceInviteStatus.Opened];

    private const int StatusCodes200 = 200;
    private const int StatusCodes201 = 201;

    /// <summary>
    /// Whether an invitation, named by id rather than by token, is still completable.
    /// </summary>
    private async Task<bool> IsStillLiveAsync(Guid inviteId)
    {
        var invite = await _unitOfWork.DeviceConnectionInvites.GetByIdAsync(inviteId);
        return invite is not null
               && LiveStatuses.Contains(invite.Status)
               && invite.ExpiresAt > _timeProvider.GetUtcNow().UtcDateTime;
    }

    /// <summary>
    /// The invitation a token names, if it is still usable. Null for every other case — unknown,
    /// expired, completed, declined, revoked — because the caller must not be able to tell them
    /// apart.
    /// </summary>
    private async Task<DeviceConnectionInvite?> ResolveLiveInviteAsync(string token, CancellationToken ct)
    {
        var hash = InviteTokens.HashOrNull(token);
        if (hash is null)
            return null;

        var invite = await _unitOfWork.DeviceConnectionInvites.GetByTokenHashAsync(hash, ct);
        if (invite is null)
            return null;

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        return LiveStatuses.Contains(invite.Status) && invite.ExpiresAt > now ? invite : null;
    }

    private async Task<DeviceConnectionInvite> RequireInviteAsync(Guid cardiMemberId, Guid inviteId)
    {
        var invite = await _unitOfWork.DeviceConnectionInvites.GetByIdAsync(inviteId);

        // The member id from the route has to match the invite's own, or a caregiver linked to one
        // member could read an invitation belonging to another by guessing its id. 404 either way,
        // matching the CardiMember convention: a 403 would confirm the invitation exists.
        if (invite is null || invite.CardiMemberId != cardiMemberId)
            throw new KeyNotFoundException("Invite not found");

        return invite;
    }

    private static DeviceInviteChannel ResolveChannel(string channel) => channel?.ToLowerInvariant() switch
    {
        LinkChannel => DeviceInviteChannel.Link,
        QrChannel => DeviceInviteChannel.QrCode,
        _ => throw new DeviceConnectionException(
            DeviceConnectionException.UnsupportedInviteChannel,
            $"Channel must be '{LinkChannel}' or '{QrChannel}'."),
    };

    private static string ToWireChannel(DeviceInviteChannel channel) =>
        channel == DeviceInviteChannel.QrCode ? QrChannel : LinkChannel;

    /// <summary>
    /// The URL the wearer opens. Built from configuration where there is any, and only from the
    /// request's own origin when there is not — see <see cref="DeviceInviteOptions.PublicBaseUrl"/>
    /// for why that distinction is load-bearing rather than tidiness.
    /// </summary>
    private string BuildInviteUrl(string requestBaseUrl, string token)
    {
        var configured = _options.Value.PublicBaseUrl;
        var baseUrl = (string.IsNullOrWhiteSpace(configured) ? requestBaseUrl : configured).TrimEnd('/');

        return $"{baseUrl}/connect?t={Uri.EscapeDataString(token)}";
    }

    private DeviceInviteResponse ToResponse(DeviceConnectionInvite invite, DateTime utcNow) => new()
    {
        InviteId = invite.Id,
        Provider = DeviceProviderNames.ToWireName(invite.DeviceType),
        Channel = ToWireChannel(invite.Channel),
        // Expiry is stored as an instant and reported as a status, so the waiting screen does not
        // have to re-derive it from a device clock that may not agree with ours.
        Status = LiveStatuses.Contains(invite.Status) && invite.ExpiresAt <= utcNow
            ? "expired"
            : invite.Status.ToString().ToLowerInvariant(),
        ExpiresAt = invite.ExpiresAt,
        OpenedAt = invite.OpenedAt,
        ResolvedAt = invite.ResolvedAt,
        DeviceId = invite.DeviceConnectionId,
        ReplacesDeviceId = invite.ReplacesDeviceConnectionId,
    };

    private async Task EnsureMemberAccessAsync(Guid requestingUserId, Guid cardiMemberId)
    {
        var links = await _unitOfWork.UserCardiMembers.GetByUserIdAsync(requestingUserId);
        var link = links.FirstOrDefault(l => l.CardiMemberId == cardiMemberId && l.IsActive);
        if (link is null)
            throw new KeyNotFoundException("CardiMember not found");

        var member = await _unitOfWork.CardiMembers.GetByIdAsync(cardiMemberId);
        if (member is null || !member.IsActive)
            throw new KeyNotFoundException("CardiMember not found");
    }

    /// <summary>
    /// Records an invitation event against the caregiver who created it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Written here rather than left to <c>AuditLoggingMiddleware</c> because half these events
    /// arrive on anonymous requests: the middleware requires an authenticated subject, and a wearer
    /// opening a link has no account. The caregiver is the accountable party in every case — they
    /// created the invitation and they receive what it produces — so the trail reads as one
    /// caregiver's actions whichever half of the flow generated the entry.
    /// </para>
    /// <para>
    /// A failure to write is logged and swallowed. That is the opposite of the repository's own
    /// contract, and deliberate at this one call site: the alternative is a wearer being told their
    /// consent failed because our audit database was briefly unreachable, having already granted it
    /// at the provider. The event is still in the application log.
    /// </para>
    /// <para>
    /// <strong><see cref="AuditLog.IpAddress"/> and <see cref="AuditLog.UserAgent"/> are left empty,
    /// deliberately.</strong> On the caregiver's own calls the middleware's entry already carries
    /// them. On the wearer's, the request comes from somebody with no account who never asked to be
    /// in this flow, and recording their address and browser would collect more about them than the
    /// feature collects about anyone else — the whole design of it is that we never learn who the
    /// link went to. The entry still answers who is accountable (the caregiver), what happened, to
    /// which invitation, about which member, and when, which is what an investigation of an
    /// invitation actually asks.
    /// </para>
    /// </remarks>
    private async Task AuditAsync(
        DeviceConnectionInvite invite,
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
                UserId = invite.CreatedByUserId,
                CardiMemberId = invite.CardiMemberId,
                Action = action,
                EntityType = "DeviceConnectionInvite",
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
                "Failed to audit {Action} for device invite {InviteId}.", action, invite.Id);
        }
    }
}
