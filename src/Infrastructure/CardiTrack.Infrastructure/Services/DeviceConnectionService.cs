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

    /// <summary>
    /// The invitation statuses a wearer grant may still be claimed from. Must name the same two as
    /// <c>DeviceConnectionInviteService</c> and the table's partial index; a disagreement here would
    /// let a grant land against an invitation somebody had already finished with.
    /// </summary>
    private static readonly DeviceInviteStatus[] LiveInviteStatuses =
        [DeviceInviteStatus.Pending, DeviceInviteStatus.Opened];

    private readonly IUnitOfWork _unitOfWork;
    private readonly IEncryptionService _encryption;
    private readonly IDistributedCache _cache;
    private readonly IOAuthCodeExchangeService _codeExchange;
    private readonly IOAuthTokenRefreshService _tokenRefresh;
    private readonly ICardiMemberAccessService _access;
    private readonly INotificationGapResolver _gapResolver;
    private readonly List<DeviceProviderSettings> _providerConfigs;
    private readonly IOAuthGrantRevoker _grantRevoker;
    private readonly IDeviceAccountIdentityResolver _accountIdentity;

    public DeviceConnectionService(
        IUnitOfWork unitOfWork,
        IEncryptionService encryption,
        IDistributedCache cache,
        IOAuthCodeExchangeService codeExchange,
        IOAuthTokenRefreshService tokenRefresh,
        ICardiMemberAccessService access,
        INotificationGapResolver gapResolver,
        IOptions<List<DeviceProviderSettings>> providerConfigs,
        IOAuthGrantRevoker grantRevoker,
        IDeviceAccountIdentityResolver accountIdentity)
    {
        _grantRevoker = grantRevoker;
        _accountIdentity = accountIdentity;
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
        var intent = ParseIntent(request.Mode);

        // Reconnect and replace act on one named connection, which has to be the member's own and
        // still there. Replacing removes it, so it takes the same primary-caregiver rule as
        // removing it does.
        DeviceConnection? target = null;
        if (intent != ConnectIntent.Add)
        {
            if (intent == ConnectIntent.Replace)
                await EnsureManageAccessAsync(requestingUserId, cardiMemberId, ct);

            var connections = (await _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(cardiMemberId)).ToList();
            target = RequireConnection(connections, request.DeviceId ?? Guid.Empty);

            if (intent == ConnectIntent.Reconnect && target.DeviceType != deviceType)
            {
                throw new DeviceConnectionException(
                    DeviceConnectionException.ProviderMismatch,
                    $"This device is a {target.DeviceType.GetDisplayName()} — reconnect it as one, "
                    + "or change it for a different device instead.");
            }
        }

        var state = PkceGenerator.GenerateStateToken();
        var codeVerifier = PkceGenerator.GenerateCodeVerifier();
        var codeChallenge = PkceGenerator.GenerateCodeChallenge(codeVerifier);

        var payload = new OAuthStatePayload(
            requestingUserId, cardiMemberId, deviceType, request.RedirectUri,
            Intent: intent, TargetConnectionId: target?.Id);
        await CacheStateAsync(state, payload, ct);

        // Providers that only accept https redirects (Google) get the configured bounce URI;
        // the bounce endpoint later 302s back to the app deep link cached in the state payload.
        var providerRedirectUri = string.IsNullOrEmpty(config.RedirectUri)
            ? request.RedirectUri
            : config.RedirectUri;

        return new OAuthInitiationResponse
        {
            AuthorizationUrl = BuildAuthorizationUrl(
                config, state, codeChallenge, providerRedirectUri, NeedsFirstConsent(intent, target)),
            State = state,
            CodeVerifier = codeVerifier
        };
    }

    public async Task<string> InitiateWearerConnectionAsync(
        Guid inviteId,
        Guid creatingUserId,
        Guid cardiMemberId,
        DeviceType deviceType,
        Guid? replacesConnectionId,
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
            CodeVerifier: codeVerifier,
            Intent: replacesConnectionId is null ? ConnectIntent.Add : ConnectIntent.Replace,
            TargetConnectionId: replacesConnectionId);

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

        // Always the full consent: the wearer is choosing an account on their own phone, and only a
        // first grant is certain to come back with a refresh token.
        return BuildAuthorizationUrl(config, state, codeChallenge, config.RedirectUri, firstConsent: true);
    }

    public async Task EnsureCanReplaceAsync(
        Guid requestingUserId, Guid cardiMemberId, Guid deviceId, CancellationToken ct = default)
    {
        await EnsureManageAccessAsync(requestingUserId, cardiMemberId, ct);
        RequireConnection(
            (await _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(cardiMemberId)).ToList(), deviceId);
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

    public async Task<Guid?> PeekWearerInviteIdAsync(
        string provider, string state, CancellationToken ct = default)
    {
        if (!DeviceProviderNames.TryResolve(provider, out var deviceType))
            return null;

        // Peek, like the bounce's own resolution — the state is spent by whichever path completes
        // the flow, and a liveness check that consumed it would spend the wearer's one attempt on
        // finding out whether they still had one.
        var cached = await _cache.GetStringAsync(StateKeyPrefix + state, ct);
        if (cached is null)
            return null;

        JsonUtility.TryDeserialize<OAuthStatePayload>(cached, out var payload, out _);

        return payload is { Channel: DeviceOAuthChannel.Wearer } && SameApi(payload.Provider, deviceType)
            ? payload.InviteId
            : null;
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

        var inviteId = payload.InviteId.Value;
        var device = await ExchangeAndStoreAsync(
            payload,
            code,
            payload.CodeVerifier!,
            // The invitation's move to Completed is the claim: it only succeeds while the invitation
            // is still live, so a caregiver who revoked at any point up to this instant stops the
            // connection being stored at all.
            claimCt => _unitOfWork.DeviceConnectionInvites.TryResolveAsync(
                inviteId,
                LiveInviteStatuses,
                DeviceInviteStatus.Completed,
                DateTime.UtcNow,
                deviceConnectionId: null,
                claimCt),
            ct);

        // The connection id is known only after the write, and the claim above could not carry it.
        await _unitOfWork.DeviceConnectionInvites.RecordConnectionAsync(inviteId, device.DeviceId, ct);

        return new WearerConnectionCompletion(device, inviteId);
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
    /// The half of completion both channels share: exchange the code, then store the grant as the
    /// state's intent says — a device added alongside the others, the named device reconnected, or
    /// the named device replaced.
    /// </summary>
    /// <remarks>
    /// What differs between the channels is everything <em>before</em> this — who is authorized to
    /// be here, and where the verifier came from. What a granted connection means afterwards is
    /// identical, and was worth keeping identical: the sync worker, the token refresher and the
    /// battery capture all read these columns without knowing or caring which screen the wearer
    /// tapped consent on.
    /// </remarks>
    private async Task<DeviceResponse> ExchangeAndStoreAsync(
        OAuthStatePayload payload, string code, string codeVerifier, CancellationToken ct) =>
        await ExchangeAndStoreAsync(payload, code, codeVerifier, claim: null, ct);

    /// <summary>
    /// As above, with an optional <paramref name="claim"/> run in the same database transaction as
    /// the connection write.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The wearer flow passes one; the app flow does not. A claim that returns false aborts the
    /// whole thing — nothing is stored, and the caller is told the invitation is no longer live.
    /// </para>
    /// <para>
    /// <strong>It has to be in the transaction, not merely before it.</strong> Checking liveness and
    /// then storing leaves a gap a caregiver's revocation can land in: the check passes, the revoke
    /// commits, the connection is stored anyway, and the only thing that fails afterwards is the
    /// invitation's own status update. The device would be connected to somebody whose caregiver had
    /// withdrawn the invitation — quietly, and with the row saying "revoked" the whole time.
    /// Committing the claim and the connection together is what makes "the withdrawal wins" true
    /// rather than nearly true.
    /// </para>
    /// <para>
    /// The provider call stays outside the transaction. Holding a Postgres transaction open across
    /// an external HTTP round trip would pin a connection for the provider's latency, and a provider
    /// that hangs would hold it for the timeout.
    /// </para>
    /// </remarks>
    private async Task<DeviceResponse> ExchangeAndStoreAsync(
        OAuthStatePayload payload,
        string code,
        string codeVerifier,
        Func<CancellationToken, Task<bool>>? claim,
        CancellationToken ct)
    {
        // The brand picked at initiation — what a new connection is displayed as. It does not say
        // which connection the grant belongs to: two Fitbits are two devices. The intent and the
        // provider account decide that, below.
        var deviceType = payload.Provider;
        var config = _providerConfigs.ConfigFor(deviceType)!;

        // Replacing removes a device, so the caller's authority to remove it is re-checked now
        // rather than trusted from initiation — they may have been demoted in between.
        if (payload.Intent == ConnectIntent.Replace)
            await EnsureManageAccessAsync(payload.UserId, payload.CardiMemberId, ct);

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

        // Which account the grant is for, asked of the provider before any of our own writes, so
        // the transaction below still spans nothing but the database.
        var account = new GrantAccount(
            await _accountIdentity.TryResolveAsync(deviceType, tokens.AccessToken, ct),
            tokens.ProviderUserId);

        // The exchange is done and the transaction opens here, so it spans only our own writes. It
        // holds the member's device lock, so a second grant completing at the same moment reads
        // what this one stored — the account match and the primary flag both depend on it.
        await _unitOfWork.BeginTransactionAsync();

        DeviceConnection connection;
        StoredGrant outcome;
        try
        {
            await _unitOfWork.DeviceConnections.LockMemberDevicesAsync(payload.CardiMemberId, ct);
            var existing = (await _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(payload.CardiMemberId)).ToList();
            outcome = await ResolveGrantTargetAsync(payload, existing, deviceType, account);
            var now = DateTime.UtcNow;

            connection = outcome.Connection ?? new DeviceConnection
            {
                CardiMemberId = payload.CardiMemberId,
                DeviceType = deviceType,
                DeviceName = await ResolveDisplayNameAsync(deviceType),
                // A replacement stands where the old device stood; anything else is primary only
                // when the member has no primary to keep.
                IsPrimary = outcome.Replaced?.IsPrimary ?? !existing.Any(c => c.IsPrimary),
                ConnectedDate = now,
            };

            // An existing connection only ever gets here holding the same account the grant came
            // back on, or one that could not be compared: a reconnect known to be on another
            // account is refused in ResolveGrantTargetAsync, since carrying the old account's
            // refresh token over would leave background syncs pulling a stranger's health data
            // under this member.
            connection.ConnectionStatus = ConnectionStatus.Connected;
            connection.AccessToken = _encryption.Encrypt(tokens.AccessToken);
            // Providers that only issue a refresh token on the first grant (Google) send none on a
            // reconnect — overwriting with null would strand the connection at the next expiry.
            if (tokens.RefreshToken is not null)
                connection.RefreshToken = _encryption.Encrypt(tokens.RefreshToken);
            connection.TokenExpiry = now.AddSeconds(tokens.ExpiresInSeconds);
            connection.Scopes = scopes;
            connection.IsActive = true;
            // What this grant said about its account, and nothing older: a token response without a
            // user id clears the one a previous grant left, or the account match would go on
            // comparing later grants against an account this connection may no longer be on.
            connection.Metadata = tokens.ProviderUserId is null
                ? null
                : JsonSerializer.Serialize(new { providerUserId = tokens.ProviderUserId });

            // Captured here rather than waiting for the first sync, so the next grant can be told
            // apart from this one — and webhooks reach a new connection from its first minute.
            // Stored even when the lookup failed: a reconnect whose account could not be read may
            // be on another account, and keeping the old id would label the new grant with it for
            // good, since the sync only captures an id that is missing. Null lets it re-capture.
            connection.HealthUserId = account.HealthUserId;

            if (outcome.Connection is null)
                await _unitOfWork.DeviceConnections.AddAsync(connection);

            if (outcome.Replaced is { } replaced)
            {
                Retire(replaced, now);
                _unitOfWork.DeviceConnections.Update(replaced);
            }

            // The claim runs against the open transaction, so the invitation's move to a terminal
            // state and this connection (and any replacement's removal) either all land or none do.
            if (claim is not null && !await claim(ct))
            {
                throw new DeviceConnectionException(
                    DeviceConnectionException.InviteNotLive,
                    "That invitation is no longer available.");
            }

            await _unitOfWork.SaveChangesAsync();
            await _unitOfWork.CommitTransactionAsync();
        }
        catch
        {
            await _unitOfWork.RollbackTransactionAsync();
            throw;
        }

        // After the commit, and from a copy taken before Retire discarded the tokens: the provider
        // call stays outside the transaction, and the old grant is only ended once the new one is
        // certainly stored.
        if (outcome.RevokeAfterStore is { } revoke)
            await RevokeUnlessSharedAsync(revoke, ct);

        // A fresh connection closes the device gaps immediately — the caregiver should not land
        // back on a dashboard still telling them to reconnect.
        await _gapResolver.ResolveForCardiMemberAsync(connection.CardiMemberId, ct);

        var response = ToDeviceResponse(connection);
        response.AlreadyConnected = outcome.AlreadyConnected;
        response.ReplacedDeviceId = outcome.Replaced?.Id;
        return response;
    }

    /// <summary>
    /// Which of the member's connections a completed grant lands on, per the state's intent.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><b>Add</b> stores a new connection — unless the account is one the member already has
    /// connected, which refreshes that connection instead. One account is one data stream, and
    /// two cards over it would count every step twice and disagree about nothing.</item>
    /// <item><b>Reconnect</b> lands on the named connection, and is refused when the account is
    /// known to differ from the one it holds: that is a change of device, and doing it silently
    /// would let a second device overwrite the first — history and all.</item>
    /// <item><b>Replace</b> stores a new connection and retires the named one — or, when the account
    /// is the named connection's own, simply reconnects it. An account another of the member's
    /// connections holds is refused.</item>
    /// </list>
    /// </remarks>
    private async Task<StoredGrant> ResolveGrantTargetAsync(
        OAuthStatePayload payload, List<DeviceConnection> existing, DeviceType deviceType, GrantAccount account)
    {
        // A reconnect or replace always names its connection at initiation; one that has gone since
        // (removed while the wearer was on the consent screen) is not found rather than guessed at.
        DeviceConnection RequireTarget() => RequireConnection(existing, payload.TargetConnectionId ?? Guid.Empty);

        bool SameAccount(DeviceConnection c) => SameApi(c.DeviceType, deviceType) && account.Matches(c);

        switch (payload.Intent)
        {
            case ConnectIntent.Reconnect:
            {
                var target = RequireTarget();
                if (account.DiffersFrom(target))
                {
                    throw new DeviceConnectionException(
                        DeviceConnectionException.DifferentAccount,
                        "That's a different account from the one this device was connected with. "
                        + "To connect it instead, change the device.");
                }

                return new StoredGrant(target, Replaced: null, AlreadyConnected: false, RevokeAfterStore: null);
            }

            case ConnectIntent.Replace:
            {
                var target = RequireTarget();
                if (SameAccount(target))
                    return new StoredGrant(target, Replaced: null, AlreadyConnected: false, RevokeAfterStore: null);

                if (existing.Any(c => c.Id != target.Id && SameAccount(c)))
                {
                    throw new DeviceConnectionException(
                        DeviceConnectionException.AccountAlreadyConnected,
                        "That account is already connected to another of this person's devices.");
                }

                return new StoredGrant(
                    Connection: null,
                    Replaced: target,
                    AlreadyConnected: false,
                    RevokeAfterStore: await MayRevokeOnReplaceAsync(target, account, existing) ? TokensOf(target) : null);
            }

            default:
            {
                var sameAccount = existing.FirstOrDefault(SameAccount);
                return new StoredGrant(sameAccount, Replaced: null, AlreadyConnected: sameAccount is not null, RevokeAfterStore: null);
            }
        }
    }

    /// <summary>
    /// Whether the replaced connection's grant can be ended at the provider without harming
    /// anything still in use.
    /// </summary>
    /// <remarks>
    /// Revoking a Google refresh token ends the whole grant for that account and client, so it is
    /// only safe when the new grant is <em>known</em> to be on another account — otherwise the
    /// revocation could take the connection just stored down with it — and when nothing else may
    /// share the old grant (<see cref="GrantMayBeSharedAsync"/>). When in doubt the grant is left
    /// at the provider: the old tokens are still discarded here, so nothing more is read with it.
    /// </remarks>
    private async Task<bool> MayRevokeOnReplaceAsync(
        DeviceConnection replaced, GrantAccount account, List<DeviceConnection> memberConnections) =>
        account.HealthUserId is not null
        && replaced.HealthUserId is { } replacedAccount
        && !string.Equals(replacedAccount, account.HealthUserId, StringComparison.Ordinal)
        && !await GrantMayBeSharedAsync(replaced, memberConnections);

    /// <summary>
    /// Whether another live connection may read through <paramref name="connection"/>'s provider
    /// grant, so that revoking the grant would cut it off too.
    /// </summary>
    /// <remarks>
    /// Revocation is grant-wide, so this errs towards "shared": a sibling on the same member and
    /// API is taken to share the grant unless both accounts are known and differ — an identity is
    /// best-effort, and a connection whose identity was never captured may well be on the same
    /// account. Beyond the member, only a known match counts; an uncaptured identity there is at
    /// most minutes old, since the connect flow and the first sync both capture it.
    /// </remarks>
    private async Task<bool> GrantMayBeSharedAsync(
        DeviceConnection connection, IEnumerable<DeviceConnection> memberConnections)
    {
        var unprovenSibling = memberConnections.Any(c =>
            c.Id != connection.Id
            && c.IsActive
            && c.ConnectionStatus != ConnectionStatus.Disconnected
            && SameApi(c.DeviceType, connection.DeviceType)
            && (connection.HealthUserId is null
                || c.HealthUserId is null
                || string.Equals(c.HealthUserId, connection.HealthUserId, StringComparison.Ordinal)));

        return unprovenSibling
            || (connection.HealthUserId is { } account
                && await _unitOfWork.DeviceConnections.AnyOtherActiveWithHealthUserIdAsync(connection.Id, account));
    }

    /// <summary>
    /// Runs one change to a member's set of devices in a transaction holding the member's device
    /// lock, handing it the member's connections as read under that lock. The change saves its own
    /// writes; anything that calls out to a provider belongs after this returns, not inside it.
    /// </summary>
    private async Task<T> ChangeMemberDevicesAsync<T>(
        Guid cardiMemberId, Func<List<DeviceConnection>, Task<T>> change, CancellationToken ct)
    {
        await _unitOfWork.BeginTransactionAsync();
        try
        {
            await _unitOfWork.DeviceConnections.LockMemberDevicesAsync(cardiMemberId, ct);
            var connections = (await _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(cardiMemberId)).ToList();
            var result = await change(connections);
            await _unitOfWork.CommitTransactionAsync();
            return result;
        }
        catch
        {
            await _unitOfWork.RollbackTransactionAsync();
            throw;
        }
    }

    /// <summary>
    /// A detached copy carrying what the revoker and the shared-grant check read, taken before the
    /// tokens are discarded.
    /// </summary>
    private static DeviceConnection TokensOf(DeviceConnection connection) => new()
    {
        Id = connection.Id,
        CardiMemberId = connection.CardiMemberId,
        DeviceType = connection.DeviceType,
        HealthUserId = connection.HealthUserId,
        AccessToken = connection.AccessToken,
        RefreshToken = connection.RefreshToken,
    };

    /// <summary>
    /// Ends a retired connection's grant at the provider, re-checking first that nothing now shares
    /// it.
    /// </summary>
    /// <remarks>
    /// The decision to revoke was taken under the member's lock, but the call happens after the
    /// commit, and a grant for the same account may have been stored in between — on another
    /// member, whose lock this does not hold. Re-reading immediately before the call closes that
    /// case. What no database check can close is a grant whose code exchange with the provider
    /// already happened but whose row is not yet stored: revocation is ordered against the
    /// provider's token issuance, which precedes our knowing the account at all. That connection
    /// would fail its next sync and read <c>token_expired</c> — the caregiver is asked to reconnect,
    /// nothing is read under the wrong member.
    /// </remarks>
    private async Task RevokeUnlessSharedAsync(DeviceConnection retired, CancellationToken ct)
    {
        var memberConnections = await _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(retired.CardiMemberId);
        if (await GrantMayBeSharedAsync(retired, memberConnections))
            return;

        await _grantRevoker.TryRevokeAsync(retired, ct);
    }

    /// <summary>
    /// Takes a connection out of service: soft-deleted, no longer primary, its tokens discarded.
    /// Tokens are useless once disconnected and must not linger in the database.
    /// </summary>
    private static void Retire(DeviceConnection connection, DateTime now)
    {
        connection.IsActive = false;
        connection.IsPrimary = false;
        connection.ConnectionStatus = ConnectionStatus.Disconnected;
        connection.AccessToken = null;
        connection.RefreshToken = null;
        connection.TokenExpiry = null;
        connection.UpdatedDate = now;
    }

    public async Task DisconnectAsync(
        Guid requestingUserId, Guid cardiMemberId, Guid deviceId, CancellationToken ct = default)
    {
        await EnsureManageAccessAsync(requestingUserId, cardiMemberId, ct);

        var revoke = await ChangeMemberDevicesAsync(cardiMemberId, async connections =>
        {
            var connection = RequireConnection(connections, deviceId);

            // The grant is ended at the provider as well as forgotten here — otherwise it stays
            // live at Google, CardiTrack still listed among the apps with access to this person's
            // health data, while the app shows the device as disconnected. So the tokens are copied
            // out before Retire discards them. Except where the grant may be shared: revoking a
            // Google refresh token ends it for the whole account, cutting off every other
            // connection reading through it.
            var revokeCopy = await GrantMayBeSharedAsync(connection, connections) ? null : TokensOf(connection);

            var now = DateTime.UtcNow;
            Retire(connection, now);
            _unitOfWork.DeviceConnections.Update(connection);

            // Without this the member would be left with devices but no primary, and the sync
            // worker's primary-first ordering would silently pick an arbitrary one.
            if (!connections.Any(c => c.Id != deviceId && c.IsPrimary))
                PromotePrimary(connections, excludingId: deviceId, now);

            await _unitOfWork.SaveChangesAsync();
            return revokeCopy;
        }, ct);

        // After the commit, so no provider round trip is made while the transaction holds the lock.
        // Best effort by design: a provider outage must not stop a caregiver disconnecting a device.
        if (revoke is not null)
            await RevokeUnlessSharedAsync(revoke, ct);

        // Removing the last device is itself a gap worth raising, so re-evaluate rather than
        // assuming a disconnect only ever closes things.
        await _gapResolver.ResolveForCardiMemberAsync(cardiMemberId, ct);
    }

    public async Task<DeviceResponse> SetPrimaryAsync(
        Guid requestingUserId, Guid cardiMemberId, Guid deviceId, CancellationToken ct = default)
    {
        await EnsureManageAccessAsync(requestingUserId, cardiMemberId, ct);

        var connection = await ChangeMemberDevicesAsync(cardiMemberId, async connections =>
        {
            var connection = RequireConnection(connections, deviceId);

            // The primary is the device whose readings win a merge, and a suspended one has none coming.
            if (connection.SuspendedAt is not null)
            {
                throw new DeviceConnectionException(
                    DeviceConnectionException.DeviceSuspended,
                    "Resume this device before making it the primary one.");
            }

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
            return connection;
        }, ct);

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

    public async Task<DeviceResponse> SuspendAsync(
        Guid requestingUserId, Guid cardiMemberId, Guid deviceId, CancellationToken ct = default)
    {
        await EnsureManageAccessAsync(requestingUserId, cardiMemberId, ct);

        var (connection, changed) = await ChangeMemberDevicesAsync(cardiMemberId, async connections =>
        {
            var connection = RequireConnection(connections, deviceId);
            if (connection.SuspendedAt is not null)
                return (connection, false);

            // Suspending the one device that is still collecting would stop monitoring with no end
            // date and nobody having decided to. Pause Monitoring is bounded for exactly that
            // reason, so the caregiver is sent there instead.
            if (!connections.Any(c => c.Id != deviceId && IsCollecting(c)))
            {
                throw new DeviceConnectionException(
                    DeviceConnectionException.LastActiveDevice,
                    "This is the only device collecting data right now. To stop monitoring for a "
                    + "while, pause monitoring instead.");
            }

            var now = DateTime.UtcNow;
            connection.SuspendedAt = now;
            connection.SuspendedByUserId = requestingUserId;
            connection.UpdatedDate = now;
            _unitOfWork.DeviceConnections.Update(connection);

            if (connection.IsPrimary)
            {
                connection.IsPrimary = false;
                PromotePrimary(connections, excludingId: deviceId, now);
            }

            await _unitOfWork.SaveChangesAsync();
            return (connection, true);
        }, ct);

        // A suspended device is no longer one the device nudges should be asking about.
        if (changed)
            await _gapResolver.ResolveForCardiMemberAsync(cardiMemberId, ct);

        return ToDeviceResponse(connection, await CountTodaysUpdatesAsync(cardiMemberId, deviceId));
    }

    public async Task<DeviceResponse> ResumeAsync(
        Guid requestingUserId, Guid cardiMemberId, Guid deviceId, CancellationToken ct = default)
    {
        await EnsureManageAccessAsync(requestingUserId, cardiMemberId, ct);

        var (connection, changed) = await ChangeMemberDevicesAsync(cardiMemberId, async connections =>
        {
            var connection = RequireConnection(connections, deviceId);
            if (connection.SuspendedAt is null)
                return (connection, false);

            var now = DateTime.UtcNow;
            connection.SuspendedAt = null;
            connection.SuspendedByUserId = null;
            connection.UpdatedDate = now;
            // Only when nothing else holds the flag: resuming a device is not a vote to make it
            // primary, and taking it back unasked would undo a choice the caregiver made since.
            if (!connections.Any(c => c.Id != deviceId && c.IsPrimary))
                connection.IsPrimary = true;
            _unitOfWork.DeviceConnections.Update(connection);
            await _unitOfWork.SaveChangesAsync();
            return (connection, true);
        }, ct);

        if (changed)
            await _gapResolver.ResolveForCardiMemberAsync(cardiMemberId, ct);

        return ToDeviceResponse(connection, await CountTodaysUpdatesAsync(cardiMemberId, deviceId));
    }

    /// <summary>
    /// Whether a connection is feeding data in: not suspended, and its grant in a state the sync
    /// worker pulls from. A connection waiting on a reconnect is not collecting, so it cannot be
    /// what keeps a member monitored while another is suspended.
    /// </summary>
    private static bool IsCollecting(DeviceConnection connection) =>
        connection.IsActive
        && connection.SuspendedAt is null
        && connection.ConnectionStatus is ConnectionStatus.Connected or ConnectionStatus.SyncError;

    /// <summary>
    /// Hands the primary flag to another of the member's devices — a collecting one by preference,
    /// else any unsuspended one — so the member is never left with devices but no primary.
    /// </summary>
    private void PromotePrimary(List<DeviceConnection> connections, Guid excludingId, DateTime now)
    {
        var candidates = connections.Where(c => c.Id != excludingId && c.IsActive).ToList();
        var next = candidates.FirstOrDefault(IsCollecting)
            ?? candidates.FirstOrDefault(c => c.SuspendedAt is null);
        if (next is null)
            return;

        next.IsPrimary = true;
        next.UpdatedDate = now;
        _unitOfWork.DeviceConnections.Update(next);
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
    private static string BuildAuthorizationUrl(
        DeviceProviderSettings config,
        string state,
        string codeChallenge,
        string providerRedirectUri,
        bool firstConsent)
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

        if (firstConsent)
        {
            foreach (var (key, value) in config.FirstConsentAuthorizationParams)
            {
                authorizationUrl += $"&{Uri.EscapeDataString(key)}={Uri.EscapeDataString(value)}";
            }
        }

        return authorizationUrl;
    }

    /// <summary>
    /// Whether the authorization should carry the first-consent parameters (Google: show consent
    /// and the account chooser), which is what makes the provider issue a refresh token.
    /// </summary>
    /// <remarks>
    /// Skipped only for a reconnect of a healthy connection that still banks a refresh token:
    /// re-showing consent there buys nothing and reads as the connection having failed. A
    /// connection whose grant has failed (<c>TokenExpired</c>, <c>AuthError</c>) is asked again even
    /// with a token stored — that token is the one that stopped working, and without consent Google
    /// sends no replacement, so the reconnect would keep the dead token and fail again within the
    /// hour. Adding or replacing a device always asks — the grant may be for an account we have
    /// never held a token for, and the account chooser is how the caregiver picks which one. A
    /// sibling connection's token says nothing about this one's: each device can be a different
    /// account.
    /// </remarks>
    private static bool NeedsFirstConsent(ConnectIntent intent, DeviceConnection? target) =>
        intent != ConnectIntent.Reconnect
        || target is null
        || target.ConnectionStatus is not (ConnectionStatus.Connected or ConnectionStatus.SyncError)
        || string.IsNullOrEmpty(target.RefreshToken);

    private static ConnectIntent ParseIntent(string? mode) => mode?.ToLowerInvariant() switch
    {
        null or ConnectDeviceRequest.ModeAdd => ConnectIntent.Add,
        ConnectDeviceRequest.ModeReconnect => ConnectIntent.Reconnect,
        ConnectDeviceRequest.ModeReplace => ConnectIntent.Replace,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown connect mode."),
    };

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
        // A soft-deleted connection reads as disconnected whatever its last status was, and a
        // suspended one as suspended — the grant's own state returns once it is resumed.
        Status = !connection.IsActive ? "disconnected"
            : connection.SuspendedAt is not null ? "suspended"
            : connection.ConnectionStatus switch
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
        NextSyncAt = connection is { IsActive: true, SuspendedAt: null, LastSyncDate: { } last }
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
        SuspendedAt = connection.SuspendedAt,
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
        string? CodeVerifier = null,
        ConnectIntent Intent = ConnectIntent.Add,
        Guid? TargetConnectionId = null);

    /// <summary>What a grant is for, carried in the state from initiation to completion.</summary>
    /// <remarks>
    /// <see cref="Add"/> is zero so a state cached before this field existed deserializes as an add,
    /// which with the account match in <see cref="ResolveGrantTargetAsync"/> still lands a
    /// same-account reconnect from an older app build on the connection it came from.
    /// </remarks>
    private enum ConnectIntent
    {
        Add = 0,
        Reconnect = 1,
        Replace = 2,
    }

    /// <summary>Where a completed grant landed, and what else it did.</summary>
    /// <param name="Connection">The existing connection it updates; null to store a new one.</param>
    /// <param name="Replaced">The connection to retire in the same save, for a replacement.</param>
    /// <param name="AlreadyConnected">An add that turned out to be an account already connected.</param>
    /// <param name="RevokeAfterStore">A token-bearing copy of the replaced connection, when its grant can safely be ended.</param>
    private sealed record StoredGrant(
        DeviceConnection? Connection,
        DeviceConnection? Replaced,
        bool AlreadyConnected,
        DeviceConnection? RevokeAfterStore);

    /// <summary>
    /// The provider account a grant came back on, as far as it is known: the health-user id from the
    /// identity resource, and the user id some token responses carry. Either may be null.
    /// </summary>
    private sealed record GrantAccount(string? HealthUserId, string? ProviderUserId)
    {
        /// <summary>Known to be the account <paramref name="connection"/> holds.</summary>
        public bool Matches(DeviceConnection connection) =>
            (HealthUserId is not null && string.Equals(connection.HealthUserId, HealthUserId, StringComparison.Ordinal))
            || (ProviderUserId is not null
                && string.Equals(ReadProviderUserId(connection.Metadata), ProviderUserId, StringComparison.Ordinal));

        /// <summary>
        /// Known to be a different account from the one <paramref name="connection"/> holds. Not the
        /// negation of <see cref="Matches"/>: with nothing to compare, neither is known.
        /// </summary>
        public bool DiffersFrom(DeviceConnection connection) =>
            (HealthUserId is not null && connection.HealthUserId is { } held
                && !string.Equals(held, HealthUserId, StringComparison.Ordinal))
            || (ProviderUserId is not null && ReadProviderUserId(connection.Metadata) is { } stored
                && !string.Equals(stored, ProviderUserId, StringComparison.Ordinal));
    }
}
