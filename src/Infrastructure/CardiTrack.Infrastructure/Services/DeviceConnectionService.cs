using System.Text.Json;
using CardiTrack.Application.Interfaces.Clients;
using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Exceptions;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Security;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Domain.Extensions;
using CardiTrack.Infrastructure.ExternalClients;
using CardiTrack.Infrastructure.Security;
using CardiTrack.Infrastructure.Settings;
using CardiTrack.Shared.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;

namespace CardiTrack.Infrastructure.Services;

/// <summary>
/// M1-05..M1-07 device connection lifecycle per docs/execution/backend/api/devices.md.
/// PKCE state is held server-side in the distributed cache (single-use, short TTL) so the
/// callback can be tied back to the initiating user, member, and provider.
/// </summary>
public class DeviceConnectionService : IDeviceConnectionService
{
    private static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(15);
    private const string StateKeyPrefix = "deviceoauth:";

    // The anonymous bounce endpoint may only forward into the mobile app's own scheme —
    // an https/other target would make it an open redirect leaking code+state. Shared with
    // the request validator so the fail-fast and point-of-use gates can't drift apart.
    private const string AppRedirectScheme = ConnectDeviceRequest.AppRedirectScheme;

    private readonly IUnitOfWork _unitOfWork;
    private readonly IEncryptionService _encryption;
    private readonly IDistributedCache _cache;
    private readonly IOAuthCodeExchangeService _codeExchange;
    private readonly IOAuthTokenRefreshService _tokenRefresh;
    private readonly ICardiMemberAccessService _access;
    private readonly INotificationGapResolver _gapResolver;
    private readonly List<DeviceProviderSettings> _providerConfigs;
    private readonly IOAuthGrantRevoker _grantRevoker;

    public DeviceConnectionService(
        IUnitOfWork unitOfWork,
        IEncryptionService encryption,
        IDistributedCache cache,
        IOAuthCodeExchangeService codeExchange,
        IOAuthTokenRefreshService tokenRefresh,
        ICardiMemberAccessService access,
        INotificationGapResolver gapResolver,
        IOptions<List<DeviceProviderSettings>> providerConfigs,
        IOAuthGrantRevoker grantRevoker)
    {
        _grantRevoker = grantRevoker;
        _unitOfWork = unitOfWork;
        _encryption = encryption;
        _cache = cache;
        _codeExchange = codeExchange;
        _tokenRefresh = tokenRefresh;
        _access = access;
        _gapResolver = gapResolver;
        _providerConfigs = providerConfigs.Value;
    }

    public async Task<DeviceListResponse> GetDevicesAsync(
        Guid requestingUserId, Guid cardiMemberId, CancellationToken ct = default)
    {
        await EnsureMemberAccessAsync(requestingUserId, cardiMemberId);

        var connections = await _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(cardiMemberId);

        // One query for the whole list rather than one per device — M1-15 renders every
        // connection the member has.
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var todaysLogs = await _unitOfWork.ActivityLogs.GetByCardiMemberAndDateRangeAsync(
            cardiMemberId, today, today);
        var updatesByConnection = todaysLogs
            .GroupBy(l => l.DeviceConnectionId)
            .ToDictionary(g => g.Key, g => g.Count());

        // Likewise one query for every card's latest history re-pull.
        var latestRepulls = (await _unitOfWork.DeviceHistoryRepulls.GetLatestByConnectionIdsAsync(
                connections.Select(c => c.Id), ct))
            .ToDictionary(r => r.DeviceConnectionId);

        return new DeviceListResponse
        {
            Devices = connections
                .Select(c => ToDeviceResponse(
                    c,
                    updatesByConnection.TryGetValue(c.Id, out var count) ? count : 0,
                    latestRepulls.TryGetValue(c.Id, out var repull) ? repull : null))
                .ToList()
        };
    }

    /// <summary>
    /// The latest re-pull as the card should see it, or null when there is nothing worth
    /// saying — see <see cref="HistoryRepullWindow.ShouldPresent"/> for the rule. The server
    /// decides "still worth showing" so the rule can move without a mobile release.
    /// </summary>
    private DeviceHistoryRepullResponse? PresentableRepull(DeviceConnection connection, DeviceHistoryRepull? latest)
    {
        if (latest is null)
            return null;

        var cooldown = TimeSpan.FromHours(_providerConfigs.ConfigFor(connection.DeviceType)?.HistoryRepullCooldownHours ?? 0);
        var now = DateTime.UtcNow;

        return HistoryRepullWindow.ShouldPresent(latest, cooldown, now)
            ? HistoryRepullWindow.ToResponse(latest, cooldown, now)
            : null;
    }

    public async Task<OAuthInitiationResponse> InitiateConnectionAsync(
        Guid requestingUserId, Guid cardiMemberId, ConnectDeviceRequest request, CancellationToken ct = default)
    {
        await EnsureMemberAccessAsync(requestingUserId, cardiMemberId);

        var (deviceType, config) = ResolveProvider(request.Provider);

        var state = PkceGenerator.GenerateStateToken();
        var codeVerifier = PkceGenerator.GenerateCodeVerifier();
        var codeChallenge = PkceGenerator.GenerateCodeChallenge(codeVerifier);

        var payload = new OAuthStatePayload(requestingUserId, cardiMemberId, deviceType, request.RedirectUri);
        await CacheStateAsync(state, payload, ct);

        // Providers that only accept https redirects (Google) get the configured bounce URI;
        // the bounce endpoint later 302s back to the app deep link cached in the state payload.
        var providerRedirectUri = string.IsNullOrEmpty(config.RedirectUri)
            ? request.RedirectUri
            : config.RedirectUri;

        return new OAuthInitiationResponse
        {
            AuthorizationUrl = await BuildAuthorizationUrlAsync(
                config, deviceType, cardiMemberId, state, codeChallenge, providerRedirectUri),
            State = state,
            CodeVerifier = codeVerifier
        };
    }

    public async Task<string> InitiateWearerConnectionAsync(
        Guid inviteId,
        Guid creatingUserId,
        Guid cardiMemberId,
        DeviceType deviceType,
        CancellationToken ct = default)
    {
        var config = _providerConfigs.ConfigFor(deviceType);
        if (config is null || string.IsNullOrEmpty(config.ClientId))
        {
            throw new DeviceConnectionException(
                DeviceConnectionException.UnsupportedProvider,
                $"{deviceType.GetDisplayName()} is not configured for connections.");
        }

        var state = PkceGenerator.GenerateStateToken();
        var codeVerifier = PkceGenerator.GenerateCodeVerifier();
        var codeChallenge = PkceGenerator.GenerateCodeChallenge(codeVerifier);

        // No app deep link: this flow never returns to a phone, so the bounce completes it here
        // instead of forwarding. The verifier rides in the cached state for the same reason —
        // there is no authenticated client to hand it to and take it back from.
        var payload = new OAuthStatePayload(
            creatingUserId,
            cardiMemberId,
            deviceType,
            RedirectUri: string.Empty,
            Channel: DeviceOAuthChannel.Wearer,
            InviteId: inviteId,
            CodeVerifier: codeVerifier);

        await CacheStateAsync(state, payload, ct);

        // The provider's registered https redirect, which is the bounce. A wearer flow has no
        // fallback to the request's own redirect: a provider with no configured redirect URI
        // cannot serve this flow at all, because there would be nowhere for consent to land.
        if (string.IsNullOrEmpty(config.RedirectUri))
        {
            throw new DeviceConnectionException(
                DeviceConnectionException.UnsupportedProvider,
                $"{deviceType.GetDisplayName()} can't be connected from a link yet.");
        }

        return await BuildAuthorizationUrlAsync(
            config, deviceType, cardiMemberId, state, codeChallenge, config.RedirectUri);
    }

    public async Task<DeviceOAuthCallbackTarget?> ResolveCallbackTargetAsync(
        string provider, string state, CancellationToken ct = default)
    {
        if (!DeviceProviderNames.TryResolve(provider, out var deviceType))
            return null;

        // Peek only — the state stays cached and single-use consumption happens wherever the flow
        // is completed: CompleteConnectionAsync when the app posts the code back, or
        // CompleteWearerConnectionAsync when the bounce finishes it here.
        var cached = await _cache.GetStringAsync(StateKeyPrefix + state, ct);
        if (cached is null)
            return null;

        JsonUtility.TryDeserialize<OAuthStatePayload>(cached, out var payload, out _);
        // API-level match, not brand-level: the provider's registered redirect URI is one fixed
        // route per API (Google's bounce is .../oauth/redirect/fitbit), so a pixel_watch
        // initiation legitimately comes back through the fitbit segment. What must agree is the
        // data-source API the state was minted for.
        if (payload is null || !SameApi(payload.Provider, deviceType))
            return null;

        if (payload.Channel == DeviceOAuthChannel.Wearer)
            return new DeviceOAuthCallbackTarget(IsWearerFlow: true, AppRedirectUri: null);

        // The caller appends the callback parameters to whatever comes back, so a fragment is
        // rejected alongside the scheme: '#' would swallow everything after it and the app
        // would receive no state, code or error at all.
        if (!Uri.TryCreate(payload.RedirectUri, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, AppRedirectScheme, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            return null;
        }

        return new DeviceOAuthCallbackTarget(IsWearerFlow: false, AppRedirectUri: payload.RedirectUri);
    }

    public async Task<WearerConnectionCompletion> CompleteWearerConnectionAsync(
        string provider, string state, string code, CancellationToken ct = default)
    {
        var (routeDeviceType, _) = ResolveProvider(provider);

        var cacheKey = StateKeyPrefix + state;
        var cached = await _cache.GetStringAsync(cacheKey, ct);
        OAuthStatePayload? payload = null;
        if (cached is not null)
            JsonUtility.TryDeserialize(cached, out payload, out _);

        // Everything the app flow checks, plus the two facts that make this a wearer state at all.
        // A state minted for the app must not be completable here: that path's whole authorization
        // is the caller's access token, and this path has none to check.
        if (payload is null
            || payload.Channel != DeviceOAuthChannel.Wearer
            || payload.InviteId is null
            || string.IsNullOrEmpty(payload.CodeVerifier)
            || !SameApi(payload.Provider, routeDeviceType))
        {
            throw new DeviceConnectionException(
                DeviceConnectionException.InvalidStateToken, "Invalid or expired state token.");
        }

        // Single-use: a replayed state must fail even if the exchange below does too.
        await _cache.RemoveAsync(cacheKey, ct);

        // The caregiver's authority is re-checked here rather than trusted from invite creation.
        // Between the two, they may have been removed from the care circle or the member may have
        // been deactivated — and this is the moment health data would start flowing to them.
        await EnsureMemberAccessAsync(payload.UserId, payload.CardiMemberId);

        var device = await ExchangeAndStoreAsync(payload, code, payload.CodeVerifier!, ct);
        return new WearerConnectionCompletion(device, payload.InviteId.Value);
    }

    public async Task<DeviceResponse> CompleteConnectionAsync(
        Guid requestingUserId, string provider, OAuthCallbackRequest request, CancellationToken ct = default)
    {
        var (routeDeviceType, _) = ResolveProvider(provider);

        var cacheKey = StateKeyPrefix + request.State;
        var cached = await _cache.GetStringAsync(cacheKey, ct);
        OAuthStatePayload? payload = null;
        if (cached is not null)
            JsonUtility.TryDeserialize(cached, out payload, out _);
        // Same API-level match as the bounce: brands sharing an API share its callback route, so
        // the state payload — not the route segment — carries which brand was initiated.
        //
        // The channel check is the other half, and it is not redundant with the user check beside
        // it: a wearer state carries the *caregiver's* own id, so without this a caregiver could
        // post their own invitation's state here and complete a grant the wearer had started but
        // never finished giving. The two flows prove possession differently — this one by a code
        // verifier held on the phone, the other by a verifier we never released — and a state may
        // only be spent through the door it was minted for.
        if (payload is null
            || payload.Channel != DeviceOAuthChannel.App
            || payload.UserId != requestingUserId
            || !SameApi(payload.Provider, routeDeviceType))
        {
            throw new DeviceConnectionException(
                DeviceConnectionException.InvalidStateToken, "Invalid or expired state token.");
        }

        // Single-use: a replayed state must fail even if the exchange below does too.
        await _cache.RemoveAsync(cacheKey, ct);

        await EnsureMemberAccessAsync(requestingUserId, payload.CardiMemberId);

        return await ExchangeAndStoreAsync(payload, request.Code, request.CodeVerifier, ct);
    }

    /// <summary>
    /// The half of completion both channels share: exchange the code, then create or update the
    /// member's connection for that brand.
    /// </summary>
    /// <remarks>
    /// What differs between the channels is everything <em>before</em> this — who is authorized to
    /// be here, and where the verifier came from. What a granted connection means afterwards is
    /// identical, and was worth keeping identical: the sync worker, the token refresher and the
    /// battery capture all read these columns without knowing or caring which screen the wearer
    /// tapped consent on.
    /// </remarks>
    private async Task<DeviceResponse> ExchangeAndStoreAsync(
        OAuthStatePayload payload, string code, string codeVerifier, CancellationToken ct)
    {
        // The connection's identity is the brand picked at initiation.
        var deviceType = payload.Provider;
        var config = _providerConfigs.ConfigFor(deviceType)!;

        // Must match the redirect_uri sent in the authorize request.
        var exchangeRedirectUri = string.IsNullOrEmpty(config.RedirectUri)
            ? payload.RedirectUri
            : config.RedirectUri;

        OAuthTokenResult tokens;
        try
        {
            tokens = await _codeExchange.ExchangeCodeAsync(
                config, code, exchangeRedirectUri, codeVerifier, ct);
        }
        catch (OAuthExchangeException ex)
        {
            throw new DeviceConnectionException(
                DeviceConnectionException.OAuthExchangeFailed,
                $"{deviceType.GetDisplayName()} rejected the authorization code exchange.", ex);
        }

        var scopes = tokens.Scope is null
            ? JsonSerializer.Serialize(config.Scopes)
            : JsonSerializer.Serialize(tokens.Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        var existing = (await _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(payload.CardiMemberId)).ToList();
        var connection = existing.FirstOrDefault(c => c.DeviceType == deviceType);
        var isNew = connection is null;

        if (connection is null)
        {
            connection = new DeviceConnection
            {
                CardiMemberId = payload.CardiMemberId,
                DeviceType = deviceType,
                DeviceName = await ResolveDisplayNameAsync(deviceType),
                IsPrimary = existing.Count == 0,
                ConnectedDate = DateTime.UtcNow,
            };
        }

        // Reconnecting onto a different provider account invalidates everything we held for
        // the old one — carrying its refresh token over would leave background syncs pulling
        // a stranger's health data under this member.
        var switchedAccount = tokens.ProviderUserId is not null
            && ReadProviderUserId(connection.Metadata) is { } previous
            && !string.Equals(previous, tokens.ProviderUserId, StringComparison.Ordinal);

        connection.ConnectionStatus = ConnectionStatus.Connected;
        connection.AccessToken = _encryption.Encrypt(tokens.AccessToken);
        // Providers that only issue a refresh token on the first grant (Google) send none on a
        // reconnect — overwriting with null would strand the connection at the next expiry.
        if (tokens.RefreshToken is not null)
        {
            connection.RefreshToken = _encryption.Encrypt(tokens.RefreshToken);
        }
        else if (switchedAccount)
        {
            // Dropping it also makes the next initiation re-prompt for consent, which is how
            // a refresh token for the new account gets issued.
            connection.RefreshToken = null;
        }
        connection.TokenExpiry = DateTime.UtcNow.AddSeconds(tokens.ExpiresInSeconds);
        connection.Scopes = scopes;
        connection.IsActive = true;
        if (tokens.ProviderUserId is not null)
        {
            connection.Metadata = JsonSerializer.Serialize(new { providerUserId = tokens.ProviderUserId });
        }

        if (isNew)
        {
            await _unitOfWork.DeviceConnections.AddAsync(connection);
        }
        await _unitOfWork.SaveChangesAsync();

        // A fresh connection closes the device gaps immediately — the caregiver should not land
        // back on a dashboard still telling them to reconnect.
        await _gapResolver.ResolveForCardiMemberAsync(connection.CardiMemberId, ct);

        return ToDeviceResponse(connection);
    }

    public async Task DisconnectAsync(
        Guid requestingUserId, Guid cardiMemberId, Guid deviceId, CancellationToken ct = default)
    {
        await EnsureManageAccessAsync(requestingUserId, cardiMemberId, ct);
        var connections = (await _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(cardiMemberId)).ToList();
        var connection = RequireConnection(connections, deviceId);

        // Told to the provider before it is forgotten here: after the next three lines there is
        // no token left to revoke with, and the grant would stay live at Google — CardiTrack still
        // listed among the apps with access to this person's health data — while the app showed
        // the device as disconnected. Best effort by design: a provider outage must not stop a
        // caregiver disconnecting a device.
        var now = DateTime.UtcNow;
        await _grantRevoker.TryRevokeAsync(connection, ct);

        connection.IsActive = false;
        connection.IsPrimary = false;
        connection.ConnectionStatus = ConnectionStatus.Disconnected;
        // Tokens are useless once disconnected and must not linger in the database.
        connection.AccessToken = null;
        connection.RefreshToken = null;
        connection.TokenExpiry = null;
        connection.UpdatedDate = now;
        _unitOfWork.DeviceConnections.Update(connection);

        // Without this the member would be left with devices but no primary, and the sync
        // worker's primary-first ordering would silently pick an arbitrary one.
        var replacement = connections.FirstOrDefault(c => c.Id != deviceId && c.IsActive);
        if (replacement is not null && !connections.Any(c => c.Id != deviceId && c.IsActive && c.IsPrimary))
        {
            replacement.IsPrimary = true;
            replacement.UpdatedDate = now;
            _unitOfWork.DeviceConnections.Update(replacement);
        }

        await _unitOfWork.SaveChangesAsync();

        // Removing the last device is itself a gap worth raising, so re-evaluate rather than
        // assuming a disconnect only ever closes things.
        await _gapResolver.ResolveForCardiMemberAsync(cardiMemberId, ct);
    }

    public async Task<DeviceResponse> SetPrimaryAsync(
        Guid requestingUserId, Guid cardiMemberId, Guid deviceId, CancellationToken ct = default)
    {
        await EnsureManageAccessAsync(requestingUserId, cardiMemberId, ct);
        var connections = (await _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(cardiMemberId)).ToList();
        var connection = RequireConnection(connections, deviceId);

        var now = DateTime.UtcNow;
        foreach (var other in connections.Where(c => c.IsPrimary && c.Id != deviceId))
        {
            other.IsPrimary = false;
            other.UpdatedDate = now;
            _unitOfWork.DeviceConnections.Update(other);
        }

        connection.IsPrimary = true;
        connection.UpdatedDate = now;
        _unitOfWork.DeviceConnections.Update(connection);
        await _unitOfWork.SaveChangesAsync();

        return ToDeviceResponse(connection, await CountTodaysUpdatesAsync(cardiMemberId, deviceId));
    }

    public async Task<DeviceResponse> RefreshConnectionAsync(
        Guid requestingUserId, Guid cardiMemberId, Guid deviceId, CancellationToken ct = default)
    {
        await EnsureManageAccessAsync(requestingUserId, cardiMemberId, ct);
        var connections = (await _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(cardiMemberId)).ToList();
        var connection = RequireConnection(connections, deviceId);

        var config = _providerConfigs.ConfigFor(connection.DeviceType);
        if (config is null || string.IsNullOrEmpty(config.ClientId))
        {
            throw new DeviceConnectionException(
                DeviceConnectionException.UnsupportedProvider,
                $"{connection.DeviceType.GetDisplayName()} connections can't be refreshed from here.");
        }

        try
        {
            await _tokenRefresh.RefreshIfExpiredAsync(connection, config);
            connection.ConnectionStatus = ConnectionStatus.Connected;
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or OAuthExchangeException)
        {
            // The connection is genuinely broken — record that rather than reporting success,
            // so M1-15 shows the user they need to reconnect.
            connection.ConnectionStatus = ConnectionStatus.TokenExpired;
            connection.UpdatedDate = DateTime.UtcNow;
            _unitOfWork.DeviceConnections.Update(connection);
            await _unitOfWork.SaveChangesAsync();

            throw new DeviceConnectionException(
                DeviceConnectionException.OAuthExchangeFailed,
                $"We couldn't reach {connection.DeviceType.GetDisplayName()} — try reconnecting the device.", ex);
        }

        connection.UpdatedDate = DateTime.UtcNow;
        _unitOfWork.DeviceConnections.Update(connection);
        await _unitOfWork.SaveChangesAsync();

        return ToDeviceResponse(connection, await CountTodaysUpdatesAsync(cardiMemberId, deviceId));
    }

    private static DeviceConnection RequireConnection(List<DeviceConnection> connections, Guid deviceId)
    {
        var connection = connections.FirstOrDefault(c => c.Id == deviceId);
        if (connection is null)
            throw new KeyNotFoundException("Device not found");
        return connection;
    }

    private async Task<int> CountTodaysUpdatesAsync(Guid cardiMemberId, Guid deviceId)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var logs = await _unitOfWork.ActivityLogs.GetByCardiMemberAndDateRangeAsync(cardiMemberId, today, today);
        return logs.Count(l => l.DeviceConnectionId == deviceId);
    }

    /// <summary>
    /// Gate for the M1-15 actions that change how a member is monitored — disconnecting a
    /// device, moving the primary, forcing a token refresh. Stricter than
    /// <see cref="EnsureMemberAccessAsync"/>: a relative invited only to watch over someone
    /// must not be able to cut off the data feed. Denial surfaces as 404, like every other
    /// CardiMember access failure.
    /// </summary>
    private async Task EnsureManageAccessAsync(Guid requestingUserId, Guid cardiMemberId, CancellationToken ct)
    {
        await _access.RequireManageAccessAsync(requestingUserId, cardiMemberId, ct);

        var member = await _unitOfWork.CardiMembers.GetByIdAsync(cardiMemberId);
        if (member is null || !member.IsActive)
            throw new KeyNotFoundException("CardiMember not found");
    }

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
    /// Writes a state payload into the distributed cache under its token, with the flow's TTL.
    /// </summary>
    /// <remarks>
    /// One place both channels mint state, so the lifetime and the key prefix cannot drift apart
    /// between them — a wearer state that outlived an app state, or landed under a different
    /// prefix, would be a state the bounce could not resolve and a consent the wearer had already
    /// given for nothing.
    /// </remarks>
    private Task CacheStateAsync(string state, OAuthStatePayload payload, CancellationToken ct) =>
        _cache.SetStringAsync(
            StateKeyPrefix + state,
            JsonSerializer.Serialize(payload),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = StateLifetime },
            ct);

    /// <summary>
    /// The provider's authorization URL for one flow. Identical for both channels: the wearer sees
    /// the same consent screen, asking for the same scopes, on the same client, whichever device
    /// they are holding.
    /// </summary>
    private async Task<string> BuildAuthorizationUrlAsync(
        DeviceProviderSettings config,
        DeviceType deviceType,
        Guid cardiMemberId,
        string state,
        string codeChallenge,
        string providerRedirectUri)
    {
        var authorizationUrl =
            $"{config.AuthorizationUrl}?response_type=code" +
            $"&client_id={Uri.EscapeDataString(config.ClientId)}" +
            $"&redirect_uri={Uri.EscapeDataString(providerRedirectUri)}" +
            $"&scope={Uri.EscapeDataString(string.Join(' ', config.Scopes))}" +
            $"&state={state}" +
            $"&code_challenge={codeChallenge}" +
            "&code_challenge_method=S256";

        foreach (var (key, value) in config.AdditionalAuthorizationParams)
        {
            authorizationUrl += $"&{Uri.EscapeDataString(key)}={Uri.EscapeDataString(value)}";
        }

        // Re-consent params are for the first grant only. Once a refresh token is banked,
        // re-showing the consent screen buys nothing and reads as the connection having failed.
        if (!await HasRefreshTokenAsync(cardiMemberId, deviceType))
        {
            foreach (var (key, value) in config.FirstConsentAuthorizationParams)
            {
                authorizationUrl += $"&{Uri.EscapeDataString(key)}={Uri.EscapeDataString(value)}";
            }
        }

        return authorizationUrl;
    }

    private (DeviceType DeviceType, DeviceProviderSettings Config) ResolveProvider(string provider)
    {
        if (!DeviceProviderNames.TryResolve(provider, out var deviceType))
        {
            throw new DeviceConnectionException(
                DeviceConnectionException.UnsupportedProvider,
                $"'{provider}' is not a supported server-OAuth provider.");
        }

        var config = _providerConfigs.ConfigFor(deviceType);
        if (config is null || string.IsNullOrEmpty(config.ClientId))
        {
            throw new DeviceConnectionException(
                DeviceConnectionException.UnsupportedProvider,
                $"'{provider}' is not configured for connections.");
        }

        return (deviceType, config);
    }

    /// <summary>
    /// Whether two brands sync through the same configured <see cref="HealthApi"/>. False when
    /// either is unmapped — an unmapped brand shares an API with nothing, itself included.
    /// </summary>
    private bool SameApi(DeviceType left, DeviceType right) =>
        _providerConfigs.ApiFor(left) is { } api && api == _providerConfigs.ApiFor(right);

    /// <summary>The provider-side account id stashed on a connection, if one was ever recorded.</summary>
    private static string? ReadProviderUserId(string? metadata) =>
        JsonUtility.TryParse(metadata, out var token, out _)
            ? (string?)token?["providerUserId"]
            : null;

    /// <summary>
    /// Whether this member already has a live connection through the same API carrying a refresh
    /// token. API-level on purpose: consent (and Google's refresh-token issuance) is per OAuth
    /// client, so a Fitbit connection's token answers for a Pixel Watch initiation too — brand
    /// only decides display, not the grant. A disconnected row doesn't count — its token may
    /// well have been revoked at the provider, and re-consent is how we get a fresh one.
    /// </summary>
    private async Task<bool> HasRefreshTokenAsync(Guid cardiMemberId, DeviceType deviceType)
    {
        var connections = await _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(cardiMemberId);
        return connections.Any(c =>
            SameApi(c.DeviceType, deviceType)
            && c.IsActive
            && c.ConnectionStatus != ConnectionStatus.Disconnected
            && !string.IsNullOrEmpty(c.RefreshToken));
    }

    private async Task<string> ResolveDisplayNameAsync(DeviceType deviceType)
    {
        var catalogDevice = await _unitOfWork.Devices.GetByDeviceTypeAsync(deviceType);
        return catalogDevice?.DisplayName ?? deviceType.GetDisplayName();
    }

    private DeviceResponse ToDeviceResponse(
        DeviceConnection connection, int todayUpdateCount = 0, DeviceHistoryRepull? latestRepull = null) => new()
    {
        DeviceId = connection.Id,
        Provider = DeviceProviderNames.ToWireName(connection.DeviceType),
        DisplayName = connection.DeviceName,
        // A soft-deleted connection reads as disconnected whatever its last status was.
        Status = !connection.IsActive ? "disconnected" : connection.ConnectionStatus switch
        {
            ConnectionStatus.Connected => "active",
            ConnectionStatus.SyncError => "active",
            ConnectionStatus.Disconnected => "disconnected",
            _ => "token_expired",
        },
        IsPrimary = connection.IsPrimary,
        LastSyncedAt = connection.LastSyncDate,
        ConnectedAt = connection.ConnectedDate,
        TokenExpiresAt = connection.TokenExpiry,
        Scopes = ParseScopes(connection.Scopes),
        NextSyncAt = connection is { IsActive: true, LastSyncDate: { } last }
            ? last.AddMinutes(connection.SyncFrequencyMinutes)
            : null,
        TodayUpdateCount = todayUpdateCount,
        // A reading only leaves the API while it is still current. Suppressing both fields
        // together — rather than sending a percentage with a timestamp and leaving each client to
        // decide — keeps one definition of "too old to show" on the server, where it can be
        // changed without shipping a mobile release.
        BatteryLevel = DeviceBattery.IsFresh(connection.BatteryUpdatedAt, DateTime.UtcNow)
            ? connection.BatteryLevel
            : null,
        BatteryStatus = DeviceBattery.IsFresh(connection.BatteryUpdatedAt, DateTime.UtcNow)
            ? connection.BatteryStatus
            : null,
        HistoryRepull = PresentableRepull(connection, latestRepull),
    };

    /// <summary>Scopes are stored as a JSON array; a malformed value must not break the screen.</summary>
    private static List<string> ParseScopes(string? scopes) =>
        JsonUtility.TryDeserialize<List<string>>(scopes ?? "[]", out var parsed, out _) && parsed is not null
            ? parsed
            : [];

    /// <summary>Which kind of client a cached state was minted for.</summary>
    /// <remarks>
    /// <see cref="App"/> is zero so that a state cached before this field existed deserializes as
    /// the app flow, which is what it is. In-flight states outlive a deploy by up to their
    /// fifteen-minute TTL, and a wearer part-way through consent when the release rolled should not
    /// be told their link has expired.
    /// </remarks>
    private enum DeviceOAuthChannel
    {
        App = 0,
        Wearer = 1,
    }

    /// <summary>
    /// What a state token stands for, held server-side for the life of one authorization.
    /// </summary>
    /// <remarks>
    /// Never leaves this process. That is what lets <see cref="CodeVerifier"/> live here for the
    /// wearer flow: the value is only ever written into the distributed cache and read back by the
    /// bounce, so it is never exposed to the browser that is carrying the code.
    /// </remarks>
    private record OAuthStatePayload(
        Guid UserId,
        Guid CardiMemberId,
        DeviceType Provider,
        string RedirectUri,
        DeviceOAuthChannel Channel = DeviceOAuthChannel.App,
        Guid? InviteId = null,
        string? CodeVerifier = null);
}
