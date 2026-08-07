using System.Text.Json;
using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Exceptions;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
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

    // Route/body provider names per the REST contract. apple_health is on-device-bridge only
    // and deliberately absent — it must not enter the server OAuth flow.
    private static readonly Dictionary<string, DeviceType> ProviderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fitbit"] = DeviceType.Fitbit,
        ["garmin"] = DeviceType.Garmin,
        ["samsung_health"] = DeviceType.Samsung,
        ["withings"] = DeviceType.Withings,
    };

    private readonly IUnitOfWork _unitOfWork;
    private readonly IEncryptionService _encryption;
    private readonly IDistributedCache _cache;
    private readonly IOAuthCodeExchangeService _codeExchange;
    private readonly List<DeviceProviderSettings> _providerConfigs;

    public DeviceConnectionService(
        IUnitOfWork unitOfWork,
        IEncryptionService encryption,
        IDistributedCache cache,
        IOAuthCodeExchangeService codeExchange,
        IOptions<List<DeviceProviderSettings>> providerConfigs)
    {
        _unitOfWork = unitOfWork;
        _encryption = encryption;
        _cache = cache;
        _codeExchange = codeExchange;
        _providerConfigs = providerConfigs.Value;
    }

    public async Task<DeviceListResponse> GetDevicesAsync(
        Guid requestingUserId, Guid cardiMemberId, CancellationToken ct = default)
    {
        await EnsureMemberAccessAsync(requestingUserId, cardiMemberId);

        var connections = await _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(cardiMemberId);
        return new DeviceListResponse
        {
            Devices = connections.Select(ToDeviceResponse).ToList()
        };
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
        await _cache.SetStringAsync(
            StateKeyPrefix + state,
            JsonSerializer.Serialize(payload),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = StateLifetime },
            ct);

        // Providers that only accept https redirects (Google) get the configured bounce URI;
        // the bounce endpoint later 302s back to the app deep link cached in the state payload.
        var providerRedirectUri = string.IsNullOrEmpty(config.RedirectUri)
            ? request.RedirectUri
            : config.RedirectUri;

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

        return new OAuthInitiationResponse
        {
            AuthorizationUrl = authorizationUrl,
            State = state,
            CodeVerifier = codeVerifier
        };
    }

    public async Task<string?> GetAppRedirectUriAsync(string provider, string state, CancellationToken ct = default)
    {
        if (!ProviderNames.TryGetValue(provider, out var deviceType))
            return null;

        // Peek only — the state stays cached and single-use consumption happens in
        // CompleteConnectionAsync when the app posts the code back.
        var cached = await _cache.GetStringAsync(StateKeyPrefix + state, ct);
        if (cached is null)
            return null;

        JsonUtility.TryDeserialize<OAuthStatePayload>(cached, out var payload, out _);
        if (payload is null || payload.Provider != deviceType)
            return null;

        // The caller appends the callback parameters to whatever comes back, so a fragment is
        // rejected alongside the scheme: '#' would swallow everything after it and the app
        // would receive no state, code or error at all.
        if (!Uri.TryCreate(payload.RedirectUri, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, AppRedirectScheme, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            return null;
        }

        return payload.RedirectUri;
    }

    public async Task<DeviceResponse> CompleteConnectionAsync(
        Guid requestingUserId, string provider, OAuthCallbackRequest request, CancellationToken ct = default)
    {
        var (deviceType, config) = ResolveProvider(provider);

        var cacheKey = StateKeyPrefix + request.State;
        var cached = await _cache.GetStringAsync(cacheKey, ct);
        OAuthStatePayload? payload = null;
        if (cached is not null)
            JsonUtility.TryDeserialize(cached, out payload, out _);
        if (payload is null || payload.UserId != requestingUserId || payload.Provider != deviceType)
        {
            throw new DeviceConnectionException(
                DeviceConnectionException.InvalidStateToken, "Invalid or expired state token.");
        }

        // Single-use: a replayed state must fail even if the exchange below does too.
        await _cache.RemoveAsync(cacheKey, ct);

        await EnsureMemberAccessAsync(requestingUserId, payload.CardiMemberId);

        // Must match the redirect_uri sent in the authorize request.
        var exchangeRedirectUri = string.IsNullOrEmpty(config.RedirectUri)
            ? payload.RedirectUri
            : config.RedirectUri;

        OAuthTokenResult tokens;
        try
        {
            tokens = await _codeExchange.ExchangeCodeAsync(
                config, request.Code, exchangeRedirectUri, request.CodeVerifier, ct);
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

        return ToDeviceResponse(connection);
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

    private (DeviceType DeviceType, DeviceProviderSettings Config) ResolveProvider(string provider)
    {
        if (!ProviderNames.TryGetValue(provider, out var deviceType))
        {
            throw new DeviceConnectionException(
                DeviceConnectionException.UnsupportedProvider,
                $"'{provider}' is not a supported server-OAuth provider.");
        }

        var config = _providerConfigs.FirstOrDefault(p =>
            string.Equals(p.Provider, deviceType.ToString(), StringComparison.OrdinalIgnoreCase));
        if (config is null || string.IsNullOrEmpty(config.ClientId))
        {
            throw new DeviceConnectionException(
                DeviceConnectionException.UnsupportedProvider,
                $"'{provider}' is not configured for connections.");
        }

        return (deviceType, config);
    }

    /// <summary>The provider-side account id stashed on a connection, if one was ever recorded.</summary>
    private static string? ReadProviderUserId(string? metadata) =>
        JsonUtility.TryParse(metadata, out var token, out _)
            ? (string?)token?["providerUserId"]
            : null;

    /// <summary>
    /// Whether this member already has a live connection to the provider carrying a refresh
    /// token. A disconnected row doesn't count — its token may well have been revoked at the
    /// provider, and re-consent is how we get a fresh one.
    /// </summary>
    private async Task<bool> HasRefreshTokenAsync(Guid cardiMemberId, DeviceType deviceType)
    {
        var connections = await _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(cardiMemberId);
        return connections.Any(c =>
            c.DeviceType == deviceType
            && c.IsActive
            && c.ConnectionStatus != ConnectionStatus.Disconnected
            && !string.IsNullOrEmpty(c.RefreshToken));
    }

    private async Task<string> ResolveDisplayNameAsync(DeviceType deviceType)
    {
        var catalogDevice = await _unitOfWork.Devices.GetByDeviceTypeAsync(deviceType);
        return catalogDevice?.DisplayName ?? deviceType.GetDisplayName();
    }

    private static DeviceResponse ToDeviceResponse(DeviceConnection connection) => new()
    {
        DeviceId = connection.Id,
        Provider = ProviderNames.FirstOrDefault(kv => kv.Value == connection.DeviceType).Key
                   ?? connection.DeviceType.ToString().ToLowerInvariant(),
        DisplayName = connection.DeviceName,
        Status = connection.ConnectionStatus switch
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
    };

    private record OAuthStatePayload(Guid UserId, Guid CardiMemberId, DeviceType Provider, string RedirectUri);
}
