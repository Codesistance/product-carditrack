using CardiTrack.Application.Interfaces.Clients;
using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Exceptions;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Security;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Extensions;
using CardiTrack.Infrastructure.ExternalClients;
using CardiTrack.Infrastructure.Security;
using CardiTrack.Infrastructure.Services;
using CardiTrack.Infrastructure.Settings;
using CardiTrack.UnitTests.Notifications;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace CardiTrack.UnitTests.Services;

public class DeviceConnectionServiceTests
{
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _memberId = Guid.NewGuid();

    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IEncryptionService _encryption = Substitute.For<IEncryptionService>();
    private readonly IOAuthCodeExchangeService _codeExchange = Substitute.For<IOAuthCodeExchangeService>();
    private readonly IOAuthTokenRefreshService _tokenRefresh = Substitute.For<IOAuthTokenRefreshService>();
    private readonly IDeviceAccountIdentityResolver _accountIdentity = Substitute.For<IDeviceAccountIdentityResolver>();
    private readonly IDistributedCache _cache =
        new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));

    public DeviceConnectionServiceTests()
    {
        SetupCaregiverLink(isPrimaryCaregiver: true);
        _unitOfWork.CardiMembers.GetByIdAsync(_memberId).Returns(
            new CardiMember { Id = _memberId, Name = "Dad", IsActive = true });
        _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(_memberId).Returns([]);
        _unitOfWork.DeviceHistoryRepulls
            .GetLatestByConnectionIdsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _unitOfWork.Devices.GetByDeviceTypeAsync(DeviceType.Fitbit).Returns((Device?)null);
        // A wearer completion claims its invitation inside the same transaction as the connection.
        // The default here is "the invitation was still live", which is what the happy paths mean.
        _unitOfWork.DeviceConnectionInvites
            .TryResolveAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyCollection<DeviceInviteStatus>>(),
                Arg.Any<DeviceInviteStatus>(), Arg.Any<DateTime>(), Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns(true);
        _encryption.Encrypt(Arg.Any<string>()).Returns(c => $"enc({c.Arg<string>()})");
    }

    /// <summary>
    /// A caregiver who may view but not manage — the case the M1-15 mutating actions must
    /// refuse. Primary caregivers get the full set.
    /// </summary>
    private void SetupCaregiverLink(bool isPrimaryCaregiver, bool isActive = true) =>
        _unitOfWork.UserCardiMembers.GetByUserIdAsync(_userId).Returns(
        [
            new UserCardiMember
            {
                UserId = _userId,
                CardiMemberId = _memberId,
                IsActive = isActive,
                CanViewHealthData = true,
                IsPrimaryCaregiver = isPrimaryCaregiver,
            }
        ]);

    private DeviceConnectionService CreateSut(Action<DeviceProviderSettings>? configure = null)
    {
        var fitbit = new DeviceProviderSettings
        {
            Provider = "GoogleHealth",
            DeviceTypes = ["Fitbit", "GooglePixelWatch"],
            ClientId = "fitbit_client",
            ClientSecret = "secret",
            AuthorizationUrl = "https://www.fitbit.com/oauth2/authorize",
            TokenUrl = "https://api.fitbit.com/oauth2/token",
            Scopes = ["activity", "heartrate", "sleep"],
        };
        configure?.Invoke(fitbit);
        // Composed with the real access service rather than a stub: the caregiver-link rules
        // are exactly what the M1-15 mutating actions are being asserted against, so
        // substituting it away would leave nothing testing the gate.
        return new DeviceConnectionService(
            _unitOfWork,
            _encryption,
            _cache,
            _codeExchange,
            _tokenRefresh,
            new CardiMemberAccessService(_unitOfWork),
            new NoOpNotificationGapResolver(),
            Options.Create(new List<DeviceProviderSettings> { fitbit }),
            _accountIdentity);
    }

    /// <summary>The provider account the next grant comes back on, as the identity resource reports it.</summary>
    private void GrantIsForAccount(string? healthUserId) =>
        _accountIdentity.TryResolveAsync(Arg.Any<DeviceType>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(healthUserId);

    private static ConnectDeviceRequest FitbitRequest(string mode, Guid deviceId) => new()
    {
        Provider = "fitbit",
        RedirectUri = "carditrack://oauth/callback",
        Mode = mode,
        DeviceId = deviceId,
    };

    /// <summary>Initiates with <paramref name="request"/> and completes the grant straight away.</summary>
    private async Task<DeviceResponse> ConnectAsync(DeviceConnectionService sut, ConnectDeviceRequest request)
    {
        var initiation = await sut.InitiateConnectionAsync(_userId, _memberId, request);
        return await sut.CompleteConnectionAsync(_userId, "fitbit", new OAuthCallbackRequest
        {
            Code = "code",
            State = initiation.State,
            CodeVerifier = initiation.CodeVerifier,
        });
    }

    private void GrantReturns(string access = "access", string? refresh = "refresh", string? providerUserId = null) =>
        _codeExchange.ExchangeCodeAsync(Arg.Any<DeviceProviderSettings>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(new OAuthTokenResult(access, refresh, 3600, null, providerUserId));

    private static ConnectDeviceRequest FitbitRequest() => new()
    {
        Provider = "fitbit",
        RedirectUri = "carditrack://oauth/callback"
    };

    [Fact]
    public async Task InitiateConnection_BuildsPkceAuthorizationUrl()
    {
        var result = await CreateSut().InitiateConnectionAsync(_userId, _memberId, FitbitRequest());

        Assert.StartsWith("https://www.fitbit.com/oauth2/authorize?response_type=code", result.AuthorizationUrl);
        Assert.Contains("client_id=fitbit_client", result.AuthorizationUrl);
        Assert.Contains("redirect_uri=carditrack%3A%2F%2Foauth%2Fcallback", result.AuthorizationUrl);
        Assert.Contains("scope=activity%20heartrate%20sleep", result.AuthorizationUrl);
        Assert.Contains($"state={result.State}", result.AuthorizationUrl);
        Assert.Contains("code_challenge_method=S256", result.AuthorizationUrl);
        Assert.Contains($"code_challenge={PkceGenerator.GenerateCodeChallenge(result.CodeVerifier)}",
            result.AuthorizationUrl);
        Assert.NotEmpty(result.State);
        Assert.NotEmpty(result.CodeVerifier);
    }

    [Fact]
    public async Task InitiateConnection_Throws_WhenMemberNotLinkedToUser()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            CreateSut().InitiateConnectionAsync(_userId, Guid.NewGuid(), FitbitRequest()));
    }

    [Fact]
    public async Task InitiateConnection_RejectsOnDeviceBridgeProvider()
    {
        var request = new ConnectDeviceRequest { Provider = "apple_health", RedirectUri = "carditrack://oauth/callback" };

        var ex = await Assert.ThrowsAsync<DeviceConnectionException>(() =>
            CreateSut().InitiateConnectionAsync(_userId, _memberId, request));
        Assert.Equal(DeviceConnectionException.UnsupportedProvider, ex.Code);
    }

    [Fact]
    public async Task CompleteConnection_StoresEncryptedTokens_AndReturnsPrimaryActiveDevice()
    {
        _codeExchange.ExchangeCodeAsync(Arg.Any<DeviceProviderSettings>(), "code", "carditrack://oauth/callback", Arg.Any<string>())
            .Returns(new OAuthTokenResult("access", "refresh", 28800, "activity heartrate", "FITBIT1"));

        var sut = CreateSut();
        var initiation = await sut.InitiateConnectionAsync(_userId, _memberId, FitbitRequest());

        DeviceConnection? added = null;
        await _unitOfWork.DeviceConnections.AddAsync(Arg.Do<DeviceConnection>(c => added = c));

        var device = await sut.CompleteConnectionAsync(_userId, "fitbit", new OAuthCallbackRequest
        {
            Code = "code",
            State = initiation.State,
            CodeVerifier = initiation.CodeVerifier,
        });

        Assert.NotNull(added);
        Assert.Equal("enc(access)", added!.AccessToken);
        Assert.Equal("enc(refresh)", added.RefreshToken);
        Assert.Equal(ConnectionStatus.Connected, added.ConnectionStatus);
        Assert.True(added.IsPrimary);
        Assert.Equal("fitbit", device.Provider);
        Assert.Equal("active", device.Status);
        await _unitOfWork.Received(1).SaveChangesAsync();
    }

    [Fact]
    public async Task CompleteConnection_StateIsSingleUse()
    {
        _codeExchange.ExchangeCodeAsync(Arg.Any<DeviceProviderSettings>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(new OAuthTokenResult("access", "refresh", 28800, null, null));

        var sut = CreateSut();
        var initiation = await sut.InitiateConnectionAsync(_userId, _memberId, FitbitRequest());
        var callback = new OAuthCallbackRequest
        {
            Code = "code",
            State = initiation.State,
            CodeVerifier = initiation.CodeVerifier,
        };

        await sut.CompleteConnectionAsync(_userId, "fitbit", callback);

        var ex = await Assert.ThrowsAsync<DeviceConnectionException>(() =>
            sut.CompleteConnectionAsync(_userId, "fitbit", callback));
        Assert.Equal(DeviceConnectionException.InvalidStateToken, ex.Code);
    }

    [Fact]
    public async Task CompleteConnection_RejectsStateIssuedToAnotherUser()
    {
        var sut = CreateSut();
        var initiation = await sut.InitiateConnectionAsync(_userId, _memberId, FitbitRequest());

        var ex = await Assert.ThrowsAsync<DeviceConnectionException>(() =>
            sut.CompleteConnectionAsync(Guid.NewGuid(), "fitbit", new OAuthCallbackRequest
            {
                Code = "code",
                State = initiation.State,
                CodeVerifier = initiation.CodeVerifier,
            }));
        Assert.Equal(DeviceConnectionException.InvalidStateToken, ex.Code);
    }

    [Fact]
    public async Task CompleteConnection_MapsExchangeFailure()
    {
        _codeExchange.ExchangeCodeAsync(Arg.Any<DeviceProviderSettings>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns<Task<OAuthTokenResult>>(_ => throw new OAuthExchangeException("rejected"));

        var sut = CreateSut();
        var initiation = await sut.InitiateConnectionAsync(_userId, _memberId, FitbitRequest());

        var ex = await Assert.ThrowsAsync<DeviceConnectionException>(() =>
            sut.CompleteConnectionAsync(_userId, "fitbit", new OAuthCallbackRequest
            {
                Code = "code",
                State = initiation.State,
                CodeVerifier = initiation.CodeVerifier,
            }));
        Assert.Equal(DeviceConnectionException.OAuthExchangeFailed, ex.Code);
    }

    [Fact]
    public async Task CompleteConnection_ReusesExistingConnection_ForSameAccount()
    {
        // One account is one data stream: adding it again refreshes the card it already has
        // rather than storing a second one that would count every step twice.
        var existing = new DeviceConnection
        {
            CardiMemberId = _memberId,
            DeviceType = DeviceType.Fitbit,
            DeviceName = "Fitbit",
            IsPrimary = true,
            ConnectionStatus = ConnectionStatus.TokenExpired,
            HealthUserId = "ACCOUNT_A",
        };
        _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(_memberId).Returns([existing]);
        GrantIsForAccount("ACCOUNT_A");
        _codeExchange.ExchangeCodeAsync(Arg.Any<DeviceProviderSettings>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(new OAuthTokenResult("access2", "refresh2", 28800, null, null));

        var sut = CreateSut();
        var initiation = await sut.InitiateConnectionAsync(_userId, _memberId, FitbitRequest());

        var device = await sut.CompleteConnectionAsync(_userId, "fitbit", new OAuthCallbackRequest
        {
            Code = "code",
            State = initiation.State,
            CodeVerifier = initiation.CodeVerifier,
        });

        await _unitOfWork.DeviceConnections.DidNotReceive().AddAsync(Arg.Any<DeviceConnection>());
        Assert.Equal(ConnectionStatus.Connected, existing.ConnectionStatus);
        Assert.Equal("enc(access2)", existing.AccessToken);
        Assert.Equal("active", device.Status);
        Assert.True(device.AlreadyConnected);
    }

    // pixel_watch is a second brand on the same GoogleHealth block — the DeviceTypes mapping,
    // not new code, is what makes it connectable.
    [Fact]
    public async Task InitiateConnection_PixelWatch_UsesTheGoogleHealthConfig()
    {
        var request = new ConnectDeviceRequest
        {
            Provider = "pixel_watch",
            RedirectUri = "carditrack://oauth/callback"
        };

        var result = await CreateSut().InitiateConnectionAsync(_userId, _memberId, request);

        Assert.StartsWith("https://www.fitbit.com/oauth2/authorize?response_type=code", result.AuthorizationUrl);
        Assert.Contains("client_id=fitbit_client", result.AuthorizationUrl);
    }

    // Google registers one bounce route per OAuth client (…/oauth/redirect/fitbit), so a
    // pixel_watch initiation legitimately completes through the fitbit segment. The connection
    // must still come out as the brand the wearer picked.
    [Fact]
    public async Task CompleteConnection_PixelWatch_KeepsTheInitiatedBrand_AcrossTheSharedBounceSegment()
    {
        _codeExchange.ExchangeCodeAsync(Arg.Any<DeviceProviderSettings>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(new OAuthTokenResult("access", "refresh", 28800, null, null));

        var sut = CreateSut();
        var initiation = await sut.InitiateConnectionAsync(_userId, _memberId, new ConnectDeviceRequest
        {
            Provider = "pixel_watch",
            RedirectUri = "carditrack://oauth/callback"
        });

        DeviceConnection? added = null;
        await _unitOfWork.DeviceConnections.AddAsync(Arg.Do<DeviceConnection>(c => added = c));

        var device = await sut.CompleteConnectionAsync(_userId, "fitbit", new OAuthCallbackRequest
        {
            Code = "code",
            State = initiation.State,
            CodeVerifier = initiation.CodeVerifier,
        });

        Assert.NotNull(added);
        Assert.Equal(DeviceType.GooglePixelWatch, added!.DeviceType);
        Assert.Equal("Google Pixel Watch", added.DeviceName);
        Assert.Equal("pixel_watch", device.Provider);
    }

    [Fact]
    public async Task ResolveCallbackTarget_AllowsTheBounce_ThroughASiblingBrandOnTheSameApi()
    {
        var sut = CreateSut();
        var initiation = await sut.InitiateConnectionAsync(_userId, _memberId, new ConnectDeviceRequest
        {
            Provider = "pixel_watch",
            RedirectUri = "carditrack://oauth/callback"
        });

        var target = await sut.ResolveCallbackTargetAsync("fitbit", initiation.State);

        Assert.NotNull(target);
        Assert.False(target.IsWearerFlow);
        Assert.Equal("carditrack://oauth/callback", target.AppRedirectUri);
    }

    [Fact]
    public async Task ResolveCallbackTarget_RejectsABrandOnADifferentApi()
    {
        var sut = CreateSut();
        var initiation = await sut.InitiateConnectionAsync(_userId, _memberId, new ConnectDeviceRequest
        {
            Provider = "pixel_watch",
            RedirectUri = "carditrack://oauth/callback"
        });

        // garmin resolves to a DeviceType no configured block claims, so it shares an API with
        // nothing — the state must not be released to its route.
        Assert.Null(await sut.ResolveCallbackTargetAsync("garmin", initiation.State));
    }

    [Fact]
    public async Task InitiateConnection_UsesConfiguredProviderRedirect_AndExtraAuthorizeParams()
    {
        var sut = CreateSut(s =>
        {
            s.RedirectUri = "https://api.example.com/api/v1/oauth/redirect/fitbit";
            s.AdditionalAuthorizationParams = new Dictionary<string, string>
            {
                ["access_type"] = "offline",
                ["prompt"] = "consent",
            };
        });

        var result = await sut.InitiateConnectionAsync(_userId, _memberId, FitbitRequest());

        Assert.Contains(
            $"redirect_uri={Uri.EscapeDataString("https://api.example.com/api/v1/oauth/redirect/fitbit")}",
            result.AuthorizationUrl);
        Assert.DoesNotContain("redirect_uri=carditrack", result.AuthorizationUrl);
        Assert.Contains("&access_type=offline", result.AuthorizationUrl);
        Assert.Contains("&prompt=consent", result.AuthorizationUrl);
    }

    [Fact]
    public async Task CompleteConnection_ExchangesWithConfiguredProviderRedirect()
    {
        const string bounce = "https://api.example.com/api/v1/oauth/redirect/fitbit";
        _codeExchange.ExchangeCodeAsync(Arg.Any<DeviceProviderSettings>(), "code", bounce, Arg.Any<string>())
            .Returns(new OAuthTokenResult("access", "refresh", 3600, null, null));

        var sut = CreateSut(s => s.RedirectUri = bounce);
        var initiation = await sut.InitiateConnectionAsync(_userId, _memberId, FitbitRequest());

        await sut.CompleteConnectionAsync(_userId, "fitbit", new OAuthCallbackRequest
        {
            Code = "code",
            State = initiation.State,
            CodeVerifier = initiation.CodeVerifier,
        });

        await _codeExchange.Received(1).ExchangeCodeAsync(
            Arg.Any<DeviceProviderSettings>(), "code", bounce, Arg.Any<string>());
    }

    [Fact]
    public async Task CompleteConnection_KeepsStoredRefreshToken_WhenProviderIssuesNone()
    {
        // Google only issues a refresh token alongside a consent prompt, so a reconnect comes
        // back without one — nulling the stored token would strand the next silent refresh. Kept
        // because the grant is positively the account that token belongs to.
        var existing = new DeviceConnection
        {
            CardiMemberId = _memberId,
            DeviceType = DeviceType.Fitbit,
            DeviceName = "Fitbit",
            IsActive = true,
            ConnectionStatus = ConnectionStatus.Connected,
            AccessToken = "enc(old_access)",
            RefreshToken = "enc(old_refresh)",
            HealthUserId = "ACCOUNT_A",
        };
        _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(_memberId).Returns([existing]);
        GrantIsForAccount("ACCOUNT_A");
        GrantReturns(access: "new_access", refresh: null);

        await ConnectAsync(CreateSut(), FitbitRequest(ConnectDeviceRequest.ModeReconnect, existing.Id));

        await _unitOfWork.DeviceConnections.DidNotReceive().AddAsync(Arg.Any<DeviceConnection>());
        Assert.Equal("enc(new_access)", existing.AccessToken);
        Assert.Equal("enc(old_refresh)", existing.RefreshToken);
    }

    [Fact]
    public async Task CompleteConnection_RefusesAReconnect_OnAnotherAccount()
    {
        // Switching the account under an existing card would leave it — and its history —
        // showing a stranger's health data under this member. That is a change of device, and
        // the caller is told so rather than it happening silently.
        var existing = new DeviceConnection
        {
            CardiMemberId = _memberId,
            DeviceType = DeviceType.Fitbit,
            DeviceName = "Fitbit",
            IsActive = true,
            ConnectionStatus = ConnectionStatus.Connected,
            RefreshToken = "enc(old_refresh)",
            Metadata = """{"providerUserId":"ACCOUNT_A"}""",
        };
        _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(_memberId).Returns([existing]);
        _codeExchange.ExchangeCodeAsync(Arg.Any<DeviceProviderSettings>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(new OAuthTokenResult("new_access", null, 3600, null, "ACCOUNT_B"));

        var ex = await Assert.ThrowsAsync<DeviceConnectionException>(() =>
            ConnectAsync(CreateSut(), FitbitRequest(ConnectDeviceRequest.ModeReconnect, existing.Id)));

        Assert.Equal(DeviceConnectionException.DifferentAccount, ex.Code);
        Assert.True(ex.IsConflict);
        Assert.Equal("enc(old_refresh)", existing.RefreshToken);
        // Nothing of the refused grant is committed; only its queued revocation is saved,
        // after the rollback.
        await _unitOfWork.DidNotReceive().CommitTransactionAsync();
        await _unitOfWork.Received(1).RollbackTransactionAsync();
    }

    [Fact]
    public async Task InitiateConnection_AddsFirstConsentParams_WhenNoRefreshTokenHeld()
    {
        var sut = CreateSut(ForcesConsent);

        var result = await sut.InitiateConnectionAsync(_userId, _memberId, FitbitRequest());

        Assert.Contains("&prompt=consent", result.AuthorizationUrl);
    }

    [Fact]
    public async Task InitiateConnection_OmitsFirstConsentParams_WhenReconnectingADeviceThatHoldsARefreshToken()
    {
        var existing = new DeviceConnection
        {
            CardiMemberId = _memberId,
            DeviceType = DeviceType.Fitbit,
            DeviceName = "Fitbit",
            IsActive = true,
            ConnectionStatus = ConnectionStatus.Connected,
            RefreshToken = "enc(refresh)",
        };
        _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(_memberId).Returns([existing]);

        var result = await CreateSut(ForcesConsent).InitiateConnectionAsync(
            _userId, _memberId, FitbitRequest(ConnectDeviceRequest.ModeReconnect, existing.Id));

        Assert.DoesNotContain("prompt=consent", result.AuthorizationUrl);
        // The unconditional params are unaffected.
        Assert.Contains("&access_type=offline", result.AuthorizationUrl);
    }

    [Theory]
    // Disconnected: the provider may well have revoked the token, so re-consent is how we
    // get a usable one back. Connected but tokenless: there is nothing to preserve.
    [InlineData(ConnectionStatus.Disconnected, "enc(refresh)")]
    [InlineData(ConnectionStatus.Connected, null)]
    // Failed grants: the stored token is the one that stopped working, and without consent Google
    // sends no replacement — the reconnect would keep the dead token and fail again.
    [InlineData(ConnectionStatus.TokenExpired, "enc(refresh)")]
    [InlineData(ConnectionStatus.AuthError, "enc(refresh)")]
    public async Task InitiateConnection_AddsFirstConsentParams_WhenReconnectingADeviceThatCannotRefresh(
        ConnectionStatus status, string? refreshToken)
    {
        var existing = new DeviceConnection
        {
            CardiMemberId = _memberId,
            DeviceType = DeviceType.Fitbit,
            DeviceName = "Fitbit",
            IsActive = true,
            ConnectionStatus = status,
            RefreshToken = refreshToken,
        };
        _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(_memberId).Returns([existing]);

        var result = await CreateSut(ForcesConsent).InitiateConnectionAsync(
            _userId, _memberId, FitbitRequest(ConnectDeviceRequest.ModeReconnect, existing.Id));

        Assert.Contains("&prompt=consent", result.AuthorizationUrl);
    }

    private static void ForcesConsent(DeviceProviderSettings settings)
    {
        settings.AdditionalAuthorizationParams = new Dictionary<string, string> { ["access_type"] = "offline" };
        settings.FirstConsentAuthorizationParams = new Dictionary<string, string> { ["prompt"] = "consent" };
    }

    [Fact]
    public async Task ResolveCallbackTarget_ReturnsDeepLink_WithoutConsumingState()
    {
        _codeExchange.ExchangeCodeAsync(Arg.Any<DeviceProviderSettings>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(new OAuthTokenResult("access", "refresh", 3600, null, null));

        var sut = CreateSut();
        var initiation = await sut.InitiateConnectionAsync(_userId, _memberId, FitbitRequest());

        var target = await sut.ResolveCallbackTargetAsync("fitbit", initiation.State);

        Assert.NotNull(target);
        Assert.False(target.IsWearerFlow);
        Assert.Equal("carditrack://oauth/callback", target.AppRedirectUri);

        // The peek must not consume the state — the app still completes the flow afterwards.
        await sut.CompleteConnectionAsync(_userId, "fitbit", new OAuthCallbackRequest
        {
            Code = "code",
            State = initiation.State,
            CodeVerifier = initiation.CodeVerifier,
        });
    }

    [Fact]
    public async Task ResolveCallbackTarget_RejectsNonAppSchemeRedirect()
    {
        // An https redirect cached at initiation must not turn the anonymous bounce
        // endpoint into an open redirect leaking code+state.
        var sut = CreateSut();
        var initiation = await sut.InitiateConnectionAsync(_userId, _memberId, new ConnectDeviceRequest
        {
            Provider = "fitbit",
            RedirectUri = "https://attacker.example.com/collect",
        });

        Assert.Null(await sut.ResolveCallbackTargetAsync("fitbit", initiation.State));
    }

    [Fact]
    public async Task ResolveCallbackTarget_RejectsRedirectCarryingAFragment()
    {
        // The bounce appends the callback parameters to whatever comes back, so a '#' would
        // swallow them and the app would receive no state, code or error at all.
        var sut = CreateSut();
        var initiation = await sut.InitiateConnectionAsync(_userId, _memberId, new ConnectDeviceRequest
        {
            Provider = "fitbit",
            RedirectUri = "carditrack://oauth/callback#done",
        });

        Assert.Null(await sut.ResolveCallbackTargetAsync("fitbit", initiation.State));
    }

    [Fact]
    public async Task ResolveCallbackTarget_ReturnsNull_ForUnknownStateOrProviderMismatch()
    {
        var sut = CreateSut();
        var initiation = await sut.InitiateConnectionAsync(_userId, _memberId, FitbitRequest());

        Assert.Null(await sut.ResolveCallbackTargetAsync("fitbit", "not-a-real-state"));
        Assert.Null(await sut.ResolveCallbackTargetAsync("garmin", initiation.State));
        Assert.Null(await sut.ResolveCallbackTargetAsync("not_a_provider", initiation.State));
    }

    // The wearer flow: the same consent, on the wearer's own device instead of the caregiver's
    // phone. What these assert is the boundary between the two — a state minted for one must not be
    // completable through the other's door.

    private static void WithBounceRedirect(DeviceProviderSettings settings) =>
        settings.RedirectUri = "https://api.example.com/api/v1/oauth/redirect/fitbit";

    [Fact]
    public async Task InitiateWearerConnection_KeepsTheVerifierOffTheWire()
    {
        var sut = CreateSut(WithBounceRedirect);
        var inviteId = Guid.NewGuid();

        var url = await sut.InitiateWearerConnectionAsync(
            inviteId, _userId, _memberId, DeviceType.Fitbit, replacesConnectionId: null);

        Assert.Contains("code_challenge=", url);
        Assert.Contains("code_challenge_method=S256", url);
        // The browser carrying the code has no account and no authenticated request to post a
        // verifier back on, so the verifier stays server-side. Handing it to the page would put it
        // in a response whose return we cannot authenticate, which is what PKCE exists to prevent.
        Assert.DoesNotContain("code_verifier", url);
        Assert.Contains("redirect_uri=" + Uri.EscapeDataString(
            "https://api.example.com/api/v1/oauth/redirect/fitbit"), url);
    }

    [Fact]
    public async Task InitiateWearerConnection_RefusesAProviderWithNoBounceRedirect()
    {
        // No configured https redirect means there is nowhere for the wearer's consent to land:
        // the app flow can fall back to a deep link, and this one has no app to fall back to.
        var sut = CreateSut();

        var ex = await Assert.ThrowsAsync<DeviceConnectionException>(
            () => sut.InitiateWearerConnectionAsync(
                Guid.NewGuid(), _userId, _memberId, DeviceType.Fitbit, replacesConnectionId: null));

        Assert.Equal(DeviceConnectionException.UnsupportedProvider, ex.Code);
    }

    [Fact]
    public async Task ResolveCallbackTarget_ReportsAWearerState_WithNoDeepLink()
    {
        var sut = CreateSut(WithBounceRedirect);
        var url = await sut.InitiateWearerConnectionAsync(
            Guid.NewGuid(), _userId, _memberId, DeviceType.Fitbit, replacesConnectionId: null);

        var target = await sut.ResolveCallbackTargetAsync("fitbit", StateFrom(url));

        Assert.NotNull(target);
        Assert.True(target.IsWearerFlow);
        // Nothing for the bounce to redirect a browser into — this flow finishes server-side.
        Assert.Null(target.AppRedirectUri);
    }

    [Fact]
    public async Task CompleteWearerConnection_StoresTheConnection_AndNamesItsInvite()
    {
        _codeExchange.ExchangeCodeAsync(
                Arg.Any<DeviceProviderSettings>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(new OAuthTokenResult("access", "refresh", 3600, null, null));

        var inviteId = Guid.NewGuid();
        var sut = CreateSut(WithBounceRedirect);
        var url = await sut.InitiateWearerConnectionAsync(
            inviteId, _userId, _memberId, DeviceType.Fitbit, replacesConnectionId: null);

        var completion = await sut.CompleteWearerConnectionAsync("fitbit", StateFrom(url), "auth_code");

        Assert.Equal(inviteId, completion.InviteId);
        Assert.Equal("active", completion.Device.Status);
        await _unitOfWork.Received().SaveChangesAsync();
    }

    [Fact]
    public async Task CompleteWearerConnection_StoresNothing_WhenTheInvitationWasRevokedMidFlight()
    {
        _codeExchange.ExchangeCodeAsync(
                Arg.Any<DeviceProviderSettings>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(new OAuthTokenResult("access", "refresh", 3600, null, null));

        // The caregiver revoked while the wearer was on the provider's consent screen, so the claim
        // finds nothing live to move.
        _unitOfWork.DeviceConnectionInvites
            .TryResolveAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyCollection<DeviceInviteStatus>>(),
                Arg.Any<DeviceInviteStatus>(), Arg.Any<DateTime>(), Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns(false);

        var sut = CreateSut(WithBounceRedirect);
        var url = await sut.InitiateWearerConnectionAsync(
            Guid.NewGuid(), _userId, _memberId, DeviceType.Fitbit, replacesConnectionId: null);

        var ex = await Assert.ThrowsAsync<DeviceConnectionException>(
            () => sut.CompleteWearerConnectionAsync("fitbit", StateFrom(url), "auth_code"));

        Assert.Equal(DeviceConnectionException.InviteNotLive, ex.Code);

        // The claim and the connection share a transaction, so losing the claim rolls the write
        // back rather than leaving a device connected under a withdrawn invitation.
        await _unitOfWork.Received().RollbackTransactionAsync();
        await _unitOfWork.DidNotReceive().CommitTransactionAsync();
    }

    [Fact]
    public async Task CompleteWearerConnection_RejectsAReplayedState()
    {
        _codeExchange.ExchangeCodeAsync(
                Arg.Any<DeviceProviderSettings>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(new OAuthTokenResult("access", "refresh", 3600, null, null));

        var sut = CreateSut(WithBounceRedirect);
        var url = await sut.InitiateWearerConnectionAsync(
            Guid.NewGuid(), _userId, _memberId, DeviceType.Fitbit, replacesConnectionId: null);
        var state = StateFrom(url);

        await sut.CompleteWearerConnectionAsync("fitbit", state, "auth_code");

        var ex = await Assert.ThrowsAsync<DeviceConnectionException>(
            () => sut.CompleteWearerConnectionAsync("fitbit", state, "auth_code"));

        Assert.Equal(DeviceConnectionException.InvalidStateToken, ex.Code);
    }

    [Fact]
    public async Task CompleteWearerConnection_RefusesAnAppState()
    {
        var sut = CreateSut(WithBounceRedirect);
        var initiation = await sut.InitiateConnectionAsync(_userId, _memberId, FitbitRequest());

        // The app flow's whole authorization is the caller's access token, and this door has none
        // to check. A state minted there must not be completable here.
        var ex = await Assert.ThrowsAsync<DeviceConnectionException>(
            () => sut.CompleteWearerConnectionAsync("fitbit", initiation.State, "auth_code"));

        Assert.Equal(DeviceConnectionException.InvalidStateToken, ex.Code);
    }

    [Fact]
    public async Task CompleteConnection_RefusesAWearerState()
    {
        var sut = CreateSut(WithBounceRedirect);
        var url = await sut.InitiateWearerConnectionAsync(
            Guid.NewGuid(), _userId, _memberId, DeviceType.Fitbit, replacesConnectionId: null);

        // And the other direction: the app must not be able to spend an invitation's state, which
        // would let a caregiver complete a grant the wearer never finished giving.
        var ex = await Assert.ThrowsAsync<DeviceConnectionException>(
            () => sut.CompleteConnectionAsync(_userId, "fitbit", new OAuthCallbackRequest
            {
                Code = "auth_code",
                State = StateFrom(url),
                CodeVerifier = "whatever",
            }));

        Assert.Equal(DeviceConnectionException.InvalidStateToken, ex.Code);
    }

    [Fact]
    public async Task CompleteWearerConnection_RefusesOnceTheCaregiverLostAccess()
    {
        var sut = CreateSut(WithBounceRedirect);
        var url = await sut.InitiateWearerConnectionAsync(
            Guid.NewGuid(), _userId, _memberId, DeviceType.Fitbit, replacesConnectionId: null);

        // The link is withdrawn between the invitation and the wearer finishing. This is the moment
        // health data would start flowing to somebody already cut off.
        SetupCaregiverLink(isPrimaryCaregiver: false, isActive: false);

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => sut.CompleteWearerConnectionAsync("fitbit", StateFrom(url), "auth_code"));
    }

    /// <summary>The state token out of an authorization URL the service just built.</summary>
    private static string StateFrom(string authorizationUrl) =>
        System.Web.HttpUtility.ParseQueryString(new Uri(authorizationUrl).Query)["state"]!;

    [Fact]
    public void AddGoogleHealthProvider_FailsFast_WhenGoogleHealthIsNotFirstProvider()
    {
        var services = new ServiceCollection();
        services.AddGoogleHealthProvider();
        services.Configure<List<DeviceProviderSettings>>(list =>
            list.Add(new DeviceProviderSettings { Provider = "GarminConnect", DeviceTypes = ["Garmin"] }));

        using var sp = services.BuildServiceProvider();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            _ = sp.GetRequiredService<IOptions<List<DeviceProviderSettings>>>().Value);
        Assert.Contains("DeviceProviders[0]", ex.Message);
    }

    [Fact]
    public async Task GetDevices_MapsConnectionStatusToContractStrings()
    {
        _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(_memberId).Returns(
        [
            new DeviceConnection { DeviceType = DeviceType.Fitbit, DeviceName = "Fitbit", ConnectionStatus = ConnectionStatus.Connected },
            new DeviceConnection { DeviceType = DeviceType.Fitbit, DeviceName = "Fitbit", ConnectionStatus = ConnectionStatus.TokenExpired },
            new DeviceConnection { DeviceType = DeviceType.Fitbit, DeviceName = "Fitbit", ConnectionStatus = ConnectionStatus.Disconnected },
        ]);

        var result = await CreateSut().GetDevicesAsync(_userId, _memberId);

        Assert.Equal(["active", "token_expired", "disconnected"], result.Devices.Select(d => d.Status));
    }

    // ── M1-15 device management ─────────────────────────────────────────────────

    private DeviceConnection SeedConnection(
        bool isPrimary = false,
        ConnectionStatus status = ConnectionStatus.Connected,
        DeviceType deviceType = DeviceType.Fitbit) => new()
        {
            CardiMemberId = _memberId,
            DeviceType = deviceType,
            DeviceName = "Dad's Fitbit",
            ConnectionStatus = status,
            IsPrimary = isPrimary,
            IsActive = true,
            AccessToken = "enc(access)",
            RefreshToken = "enc(refresh)",
            TokenExpiry = DateTime.UtcNow.AddHours(1),
            LastSyncDate = DateTime.UtcNow.AddMinutes(-10),
            SyncFrequencyMinutes = 30,
            Scopes = """["activity","heartrate","sleep"]""",
        };

    [Fact]
    public async Task GetDevices_ProjectsScopesNextSyncAndTodaysUpdates()
    {
        var connection = SeedConnection();
        _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(_memberId).Returns([connection]);
        _unitOfWork.ActivityLogs
            .GetByCardiMemberAndDateRangeAsync(_memberId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>())
            .Returns(
            [
                new ActivityLog { CardiMemberId = _memberId, DeviceConnectionId = connection.Id },
                new ActivityLog { CardiMemberId = _memberId, DeviceConnectionId = connection.Id },
                new ActivityLog { CardiMemberId = _memberId, DeviceConnectionId = Guid.NewGuid() },
            ]);

        var device = (await CreateSut().GetDevicesAsync(_userId, _memberId)).Devices.Single();

        Assert.Equal(["activity", "heartrate", "sleep"], device.Scopes);
        Assert.Equal(connection.LastSyncDate!.Value.AddMinutes(30), device.NextSyncAt);
        Assert.Equal(2, device.TodayUpdateCount);
    }

    [Fact]
    public async Task GetDevices_ProjectsAFreshBatteryReading()
    {
        var connection = SeedConnection();
        connection.BatteryLevel = 8;
        connection.BatteryStatus = "Low";
        connection.BatteryUpdatedAt = DateTime.UtcNow.AddMinutes(-10);
        _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(_memberId).Returns([connection]);

        var device = (await CreateSut().GetDevicesAsync(_userId, _memberId)).Devices.Single();

        Assert.Equal(8, device.BatteryLevel);
        Assert.Equal("Low", device.BatteryStatus);
    }

    [Fact]
    public async Task GetDevices_WithholdsABatteryReadingTooOldToBeCurrent()
    {
        // Connections are pulled every ten minutes, so a day-old reading means syncing stopped —
        // at which point the number describes a battery that has almost certainly been charged
        // since, and the screen's own stale-sync signalling is the honest thing to show instead.
        var connection = SeedConnection();
        connection.BatteryLevel = 8;
        connection.BatteryStatus = "Low";
        connection.BatteryUpdatedAt = DateTime.UtcNow.AddHours(-25);
        _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(_memberId).Returns([connection]);

        var device = (await CreateSut().GetDevicesAsync(_userId, _memberId)).Devices.Single();

        Assert.Null(device.BatteryLevel);
        Assert.Null(device.BatteryStatus);
    }

    [Fact]
    public async Task GetDevices_ReportsNoBattery_ForAConnectionThatNeverCapturedOne()
    {
        var connection = SeedConnection();
        _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(_memberId).Returns([connection]);

        var device = (await CreateSut().GetDevicesAsync(_userId, _memberId)).Devices.Single();

        Assert.Null(device.BatteryLevel);
        Assert.Null(device.BatteryStatus);
    }

    [Fact]
    public async Task GetDevices_EmbedsEachConnectionsOpenRepull_FromOneBatchedRead()
    {
        var first = SeedConnection();
        var second = SeedConnection(deviceType: DeviceType.GooglePixelWatch);
        _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(_memberId).Returns([first, second]);
        _unitOfWork.DeviceHistoryRepulls
            .GetLatestByConnectionIdsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(
            [
                new DeviceHistoryRepull
                {
                    DeviceConnectionId = first.Id,
                    Status = HistoryRepullStatus.InProgress,
                    FromDate = new DateOnly(2026, 8, 10),
                    ToDate = new DateOnly(2026, 9, 8),
                    CompletedTo = new DateOnly(2026, 8, 26),
                    DaysWithData = 11,
                },
            ]);

        var devices = (await CreateSut().GetDevicesAsync(_userId, _memberId)).Devices;

        await _unitOfWork.DeviceHistoryRepulls.Received(1).GetLatestByConnectionIdsAsync(
            Arg.Is<IEnumerable<Guid>>(ids => ids.Contains(first.Id) && ids.Contains(second.Id)),
            Arg.Any<CancellationToken>());
        var withRepull = devices.Single(d => d.DeviceId == first.Id).HistoryRepull;
        Assert.NotNull(withRepull);
        Assert.Equal("in_progress", withRepull.Status);
        Assert.Equal(14, withRepull.DaysDone);
        Assert.Equal(30, withRepull.Days);
        Assert.Null(devices.Single(d => d.DeviceId == second.Id).HistoryRepull);
    }

    [Fact]
    public async Task GetDevices_ShowsACompletedRepullOnlyWhileItsCooldownStillBlocksAnother()
    {
        var connection = SeedConnection();
        _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(_memberId).Returns([connection]);
        var completed = new DeviceHistoryRepull
        {
            DeviceConnectionId = connection.Id,
            Status = HistoryRepullStatus.Completed,
            FromDate = new DateOnly(2026, 8, 10),
            ToDate = new DateOnly(2026, 9, 8),
            CompletedTo = new DateOnly(2026, 8, 10),
            CompletedAt = DateTime.UtcNow.AddHours(-2),
        };
        _unitOfWork.DeviceHistoryRepulls
            .GetLatestByConnectionIdsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns([completed]);

        var inside = (await CreateSut(c => c.HistoryRepullCooldownHours = 48).GetDevicesAsync(_userId, _memberId))
            .Devices.Single().HistoryRepull;
        Assert.NotNull(inside);
        Assert.Equal("completed", inside.Status);
        Assert.NotNull(inside.NextAllowedAt);

        // Past the cooldown, a finished request is history: the card offers the action plainly.
        completed.CompletedAt = DateTime.UtcNow.AddHours(-49);
        var past = (await CreateSut(c => c.HistoryRepullCooldownHours = 48).GetDevicesAsync(_userId, _memberId))
            .Devices.Single().HistoryRepull;
        Assert.Null(past);
    }

    [Fact]
    public async Task GetDevices_ShowsARecentFailedRepull_SoTheCaregiverLearnsTheOutcome()
    {
        var connection = SeedConnection();
        _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(_memberId).Returns([connection]);
        _unitOfWork.DeviceHistoryRepulls
            .GetLatestByConnectionIdsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(
            [
                new DeviceHistoryRepull
                {
                    DeviceConnectionId = connection.Id,
                    Status = HistoryRepullStatus.Failed,
                    FromDate = new DateOnly(2026, 8, 10),
                    ToDate = new DateOnly(2026, 9, 8),
                    CompletedTo = new DateOnly(2026, 8, 26),
                    CompletedAt = DateTime.UtcNow.AddHours(-3),
                    FailureReason = "GoogleHealthApiException (HTTP 503)",
                },
            ]);

        var repull = (await CreateSut().GetDevicesAsync(_userId, _memberId)).Devices.Single().HistoryRepull;

        Assert.NotNull(repull);
        Assert.Equal("failed", repull.Status);
        // A failure never starts a cooldown — the action stays available beside the notice.
        Assert.Null(repull.NextAllowedAt);
    }

    [Fact]
    public async Task GetDevices_ToleratesMalformedScopes()
    {
        var connection = SeedConnection();
        connection.Scopes = "not json";
        _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(_memberId).Returns([connection]);

        var device = (await CreateSut().GetDevicesAsync(_userId, _memberId)).Devices.Single();

        Assert.Empty(device.Scopes);
    }

    [Fact]
    public async Task Disconnect_SoftDeletesAndDiscardsTokens()
    {
        var connection = SeedConnection(isPrimary: true);
        _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(_memberId).Returns([connection]);

        await CreateSut().DisconnectAsync(_userId, _memberId, connection.Id);

        Assert.False(connection.IsActive);
        Assert.False(connection.IsPrimary);
        Assert.Equal(ConnectionStatus.Disconnected, connection.ConnectionStatus);
        Assert.Null(connection.AccessToken);
        Assert.Null(connection.RefreshToken);
        Assert.Null(connection.TokenExpiry);
    }

    [Fact]
    public async Task Disconnect_PromotesAnotherDevice_WhenThePrimaryIsRemoved()
    {
        var primary = SeedConnection(isPrimary: true);
        var other = SeedConnection(deviceType: DeviceType.Withings);
        _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(_memberId).Returns([primary, other]);

        await CreateSut().DisconnectAsync(_userId, _memberId, primary.Id);

        // Otherwise the member is left with devices but no primary.
        Assert.True(other.IsPrimary);
    }

    [Fact]
    public async Task Disconnect_Throws_ForUnknownDevice()
    {
        _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(_memberId).Returns([SeedConnection()]);

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => CreateSut().DisconnectAsync(_userId, _memberId, Guid.NewGuid()));
    }

    [Fact]
    public async Task SetPrimary_DemotesThePreviousPrimary()
    {
        var previous = SeedConnection(isPrimary: true);
        var target = SeedConnection(deviceType: DeviceType.Withings);
        _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(_memberId).Returns([previous, target]);

        var result = await CreateSut().SetPrimaryAsync(_userId, _memberId, target.Id);

        Assert.True(target.IsPrimary);
        Assert.False(previous.IsPrimary);
        Assert.True(result.IsPrimary);
    }

    [Fact]
    public async Task RefreshConnection_RenewsTokenAndReportsConnected()
    {
        var connection = SeedConnection(status: ConnectionStatus.TokenExpired);
        _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(_memberId).Returns([connection]);
        _tokenRefresh
            .RefreshIfExpiredAsync(connection, Arg.Any<DeviceProviderSettings>())
            .Returns("fresh_access_token");

        var result = await CreateSut().RefreshConnectionAsync(_userId, _memberId, connection.Id);

        Assert.Equal(ConnectionStatus.Connected, connection.ConnectionStatus);
        Assert.Equal("active", result.Status);
    }

    [Fact]
    public async Task RefreshConnection_MarksTokenExpired_WhenTheProviderCannotBeReached()
    {
        var connection = SeedConnection();
        _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(_memberId).Returns([connection]);
        _tokenRefresh
            .RefreshIfExpiredAsync(connection, Arg.Any<DeviceProviderSettings>())
            .Returns<string>(_ => throw new HttpRequestException("provider down"));

        var ex = await Assert.ThrowsAsync<DeviceConnectionException>(
            () => CreateSut().RefreshConnectionAsync(_userId, _memberId, connection.Id));

        // The user is told to reconnect, and the stored state agrees with what they were told.
        Assert.Equal(DeviceConnectionException.OAuthExchangeFailed, ex.Code);
        Assert.Equal(ConnectionStatus.TokenExpired, connection.ConnectionStatus);
    }

    // ── Manage authorization on the mutating actions (Copilot review round 1) ────

    [Fact]
    public async Task Disconnect_Throws_ForViewOnlyCaregiver()
    {
        SetupCaregiverLink(isPrimaryCaregiver: false);
        var connection = SeedConnection();
        _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(_memberId).Returns([connection]);

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => CreateSut().DisconnectAsync(_userId, _memberId, connection.Id));

        // Cutting off someone's data feed is a manage action, not a viewing one.
        Assert.True(connection.IsActive);
        Assert.NotNull(connection.AccessToken);
    }

    [Fact]
    public async Task SetPrimary_Throws_ForViewOnlyCaregiver()
    {
        SetupCaregiverLink(isPrimaryCaregiver: false);
        var connection = SeedConnection();
        _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(_memberId).Returns([connection]);

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => CreateSut().SetPrimaryAsync(_userId, _memberId, connection.Id));

        Assert.False(connection.IsPrimary);
    }

    [Fact]
    public async Task RefreshConnection_Throws_ForViewOnlyCaregiver()
    {
        SetupCaregiverLink(isPrimaryCaregiver: false);
        var connection = SeedConnection();
        _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(_memberId).Returns([connection]);

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => CreateSut().RefreshConnectionAsync(_userId, _memberId, connection.Id));

        await _tokenRefresh.DidNotReceive()
            .RefreshIfExpiredAsync(Arg.Any<DeviceConnection>(), Arg.Any<DeviceProviderSettings>());
    }

    [Fact]
    public async Task GetDevices_StillAllowed_ForViewOnlyCaregiver()
    {
        // Viewing is not gated on manage access — a relative invited to watch over someone
        // must still be able to see which devices are connected.
        SetupCaregiverLink(isPrimaryCaregiver: false);
        _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(_memberId).Returns([SeedConnection()]);

        var result = await CreateSut().GetDevicesAsync(_userId, _memberId);

        Assert.Single(result.Devices);
    }

    // Issue #1286: a member can have several devices. What a grant is for — add, reconnect or
    // replace — and which provider account it came back on decide where it lands; the brand does
    // not, since two Fitbits are two devices.

    private DeviceConnection SeedAccount(string healthUserId, bool isPrimary = false,
        ConnectionStatus status = ConnectionStatus.Connected)
    {
        var connection = SeedConnection(isPrimary, status);
        connection.HealthUserId = healthUserId;
        return connection;
    }

    [Fact]
    public async Task CompleteConnection_Add_StoresASecondDevice_ForAnotherAccountOfTheSameBrand()
    {
        var first = SeedAccount("ACCOUNT_A", isPrimary: true);
        _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(_memberId).Returns([first]);
        GrantIsForAccount("ACCOUNT_B");
        GrantReturns(access: "second_access", refresh: "second_refresh");
        DeviceConnection? added = null;
        await _unitOfWork.DeviceConnections.AddAsync(Arg.Do<DeviceConnection>(c => added = c));

        var device = await ConnectAsync(CreateSut(), FitbitRequest());

        Assert.NotNull(added);
        Assert.Equal("ACCOUNT_B", added!.HealthUserId);
        Assert.Equal("enc(second_access)", added.AccessToken);
        Assert.False(added.IsPrimary);
        // The first device is exactly as it was — this is the bug the issue reported.
        Assert.Equal("enc(access)", first.AccessToken);
        Assert.Equal("enc(refresh)", first.RefreshToken);
        Assert.True(first.IsPrimary);
        Assert.False(device.AlreadyConnected);
        Assert.Null(device.ReplacedDeviceId);
    }

    [Fact]
    public async Task CompleteConnection_Add_StoresASecondDevice_WhenTheAccountCannotBeIdentified()
    {
        // With nothing to compare, an add is taken at its word rather than guessed onto a device
        // it may not be.
        var first = SeedConnection(isPrimary: true);
        _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(_memberId).Returns([first]);
        GrantIsForAccount(null);
        GrantReturns(access: "second_access");

        await ConnectAsync(CreateSut(), FitbitRequest());

        await _unitOfWork.DeviceConnections.Received(1).AddAsync(Arg.Any<DeviceConnection>());
        Assert.Equal("enc(access)", first.AccessToken);
    }

    [Fact]
    public async Task CompleteConnection_Add_TreatsTheSameAccountOnAnotherBrandOfTheSameApi_AsAlreadyConnected()
    {
        // A Pixel Watch and a Fitbit signed in to one Google account read one data stream.
        var fitbit = SeedAccount("ACCOUNT_A", isPrimary: true);
        _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(_memberId).Returns([fitbit]);
        GrantIsForAccount("ACCOUNT_A");
        GrantReturns(access: "pixel_access");

        var sut = CreateSut();
        var initiation = await sut.InitiateConnectionAsync(_userId, _memberId, new ConnectDeviceRequest
        {
            Provider = "pixel_watch",
            RedirectUri = "carditrack://oauth/callback",
        });
        var device = await sut.CompleteConnectionAsync(_userId, "fitbit", new OAuthCallbackRequest
        {
            Code = "code",
            State = initiation.State,
            CodeVerifier = initiation.CodeVerifier,
        });

        await _unitOfWork.DeviceConnections.DidNotReceive().AddAsync(Arg.Any<DeviceConnection>());
        Assert.True(device.AlreadyConnected);
        Assert.Equal(fitbit.Id, device.DeviceId);
    }

    [Fact]
    public async Task InitiateConnection_Add_AlwaysAsksForConsent_EvenWhenAnotherDeviceHoldsARefreshToken()
    {
        // The grant may be for an account we hold no token for, and the account chooser is how the
        // caregiver picks which one — a sibling's token says nothing about this grant.
        _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(_memberId).Returns([SeedAccount("ACCOUNT_A")]);

        var result = await CreateSut(ForcesConsent).InitiateConnectionAsync(_userId, _memberId, FitbitRequest());

        Assert.Contains("&prompt=consent", result.AuthorizationUrl);
    }

    [Fact]
    public async Task CompleteConnection_Reconnect_CapturesTheAccount_OnAConnectionThatHadNone()
    {
        var existing = SeedConnection(status: ConnectionStatus.TokenExpired);
        _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(_memberId).Returns([existing]);
        GrantIsForAccount("ACCOUNT_A");
        GrantReturns(access: "new_access");

        await ConnectAsync(CreateSut(), FitbitRequest(ConnectDeviceRequest.ModeReconnect, existing.Id));

        Assert.Equal("ACCOUNT_A", existing.HealthUserId);
        Assert.Equal(ConnectionStatus.Connected, existing.ConnectionStatus);
    }

    [Fact]
    public async Task CompleteConnection_Reconnect_RefusesAnotherAccount_ByHealthUserId()
    {
        var existing = SeedAccount("ACCOUNT_A", status: ConnectionStatus.TokenExpired);
        _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(_memberId).Returns([existing]);
        GrantIsForAccount("ACCOUNT_B");
        GrantReturns(access: "new_access");

        var ex = await Assert.ThrowsAsync<DeviceConnectionException>(() =>
            ConnectAsync(CreateSut(), FitbitRequest(ConnectDeviceRequest.ModeReconnect, existing.Id)));

        Assert.Equal(DeviceConnectionException.DifferentAccount, ex.Code);
        Assert.Equal("enc(access)", existing.AccessToken);
    }

    [Fact]
    public async Task CompleteConnection_Reconnect_RefusesAnAccountAnotherOfTheMembersDevicesHolds()
    {
        // The target's own account was never captured, so only its siblings can tell.
        var target = SeedConnection(status: ConnectionStatus.TokenExpired);
        var sibling = SeedAccount("ACCOUNT_B", isPrimary: true);
        _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(_memberId).Returns([target, sibling]);
        GrantIsForAccount("ACCOUNT_B");
        GrantReturns(access: "b_access");

        var ex = await Assert.ThrowsAsync<DeviceConnectionException>(() =>
            ConnectAsync(CreateSut(), FitbitRequest(ConnectDeviceRequest.ModeReconnect, target.Id)));

        Assert.Equal(DeviceConnectionException.AccountAlreadyConnected, ex.Code);
        Assert.Null(target.HealthUserId);
        Assert.Equal("enc(access)", target.AccessToken);
        await _unitOfWork.DidNotReceive().CommitTransactionAsync();
    }

    [Fact]
    public async Task InitiateConnection_Reconnect_RefusesAnotherBrand()
    {
        var existing = SeedConnection();
        _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(_memberId).Returns([existing]);

        var ex = await Assert.ThrowsAsync<DeviceConnectionException>(() =>
            CreateSut().InitiateConnectionAsync(_userId, _memberId, new ConnectDeviceRequest
            {
                Provider = "pixel_watch",
                RedirectUri = "carditrack://oauth/callback",
                Mode = ConnectDeviceRequest.ModeReconnect,
                DeviceId = existing.Id,
            }));

        Assert.Equal(DeviceConnectionException.ProviderMismatch, ex.Code);
    }

    [Fact]
    public async Task InitiateConnection_Reconnect_Throws_ForADeviceTheMemberDoesNotHave()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            CreateSut().InitiateConnectionAsync(
                _userId, _memberId, FitbitRequest(ConnectDeviceRequest.ModeReconnect, Guid.NewGuid())));
    }

    [Fact]
    public async Task CompleteConnection_Replace_KeepsTheOldDevice_WhenTheExchangeFails()
    {
        var old = SeedAccount("ACCOUNT_A", isPrimary: true);
        _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(_memberId).Returns([old]);
        _codeExchange.ExchangeCodeAsync(Arg.Any<DeviceProviderSettings>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns<Task<OAuthTokenResult>>(_ => throw new OAuthExchangeException("rejected"));

        await Assert.ThrowsAsync<DeviceConnectionException>(() =>
            ConnectAsync(CreateSut(), FitbitRequest(ConnectDeviceRequest.ModeReplace, old.Id)));

        Assert.True(old.IsActive);
        Assert.True(old.IsPrimary);
        Assert.Equal("enc(refresh)", old.RefreshToken);
        await _unitOfWork.DidNotReceive().SaveChangesAsync();
    }

    [Fact]
    public async Task CompleteConnection_Replace_RefusesAnAccountAnotherOfTheMembersDevicesHolds()
    {
        var old = SeedAccount("ACCOUNT_A", isPrimary: true);
        var other = SeedAccount("ACCOUNT_B");
        _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(_memberId).Returns([old, other]);
        GrantIsForAccount("ACCOUNT_B");
        GrantReturns();

        var ex = await Assert.ThrowsAsync<DeviceConnectionException>(() =>
            ConnectAsync(CreateSut(), FitbitRequest(ConnectDeviceRequest.ModeReplace, old.Id)));

        Assert.Equal(DeviceConnectionException.AccountAlreadyConnected, ex.Code);
        Assert.True(old.IsActive);
        // Nothing of the refused grant is committed; only its queued revocation is saved,
        // after the rollback.
        await _unitOfWork.DidNotReceive().CommitTransactionAsync();
        await _unitOfWork.Received(1).RollbackTransactionAsync();
    }

    [Fact]
    public async Task InitiateConnection_Replace_Throws_ForViewOnlyCaregiver()
    {
        // Replacing removes a device, so it takes the same primary-caregiver rule as removing one.
        SetupCaregiverLink(isPrimaryCaregiver: false);
        var old = SeedConnection(isPrimary: true);
        _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(_memberId).Returns([old]);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            CreateSut().InitiateConnectionAsync(
                _userId, _memberId, FitbitRequest(ConnectDeviceRequest.ModeReplace, old.Id)));
    }

    [Fact]
    public async Task CompleteWearerConnection_Replace_RetiresTheNamedDevice()
    {
        var old = SeedAccount("ACCOUNT_A", isPrimary: true);
        _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(_memberId).Returns([old]);
        GrantIsForAccount("ACCOUNT_B");
        GrantReturns();
        DeviceConnection? added = null;
        await _unitOfWork.DeviceConnections.AddAsync(Arg.Do<DeviceConnection>(c => added = c));

        var sut = CreateSut(WithBounceRedirect);
        var url = await sut.InitiateWearerConnectionAsync(
            Guid.NewGuid(), _userId, _memberId, DeviceType.Fitbit, replacesConnectionId: old.Id);
        var completion = await sut.CompleteWearerConnectionAsync("fitbit", StateFrom(url), "auth_code");

        Assert.NotNull(added);
        Assert.True(added!.IsPrimary);
        Assert.False(old.IsActive);
        Assert.Equal(old.Id, completion.Device.ReplacedDeviceId);
        await _unitOfWork.Received(1).CommitTransactionAsync();
    }

    [Fact]
    public async Task EnsureCanReplace_Throws_ForViewOnlyCaregiver()
    {
        SetupCaregiverLink(isPrimaryCaregiver: false);
        var old = SeedConnection(isPrimary: true);
        _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(_memberId).Returns([old]);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            CreateSut().EnsureCanReplaceAsync(_userId, _memberId, old.Id));
    }

    [Fact]
    public async Task Disconnect_PromotesACollectingDevice_OverASuspendedOne()
    {
        var primary = SeedConnection(isPrimary: true);
        var suspended = SeedConnection();
        suspended.SuspendedAt = DateTime.UtcNow.AddDays(-1);
        var collecting = SeedConnection();
        _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(_memberId).Returns([primary, suspended, collecting]);

        await CreateSut().DisconnectAsync(_userId, _memberId, primary.Id);

        Assert.True(collecting.IsPrimary);
        Assert.False(suspended.IsPrimary);
    }

    [Fact]
    public async Task Suspend_StopsTheDevice_AndHandsPrimaryToACollectingDevice()
    {
        var primary = SeedConnection(isPrimary: true);
        var other = SeedConnection();
        _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(_memberId).Returns([primary, other]);

        var device = await CreateSut().SuspendAsync(_userId, _memberId, primary.Id);

        Assert.NotNull(primary.SuspendedAt);
        Assert.Equal(_userId, primary.SuspendedByUserId);
        Assert.False(primary.IsPrimary);
        Assert.True(other.IsPrimary);
        // Suspended, not removed: the tokens and the connection stay for when it is resumed.
        Assert.True(primary.IsActive);
        Assert.Equal("enc(refresh)", primary.RefreshToken);
        Assert.Equal("suspended", device.Status);
        Assert.Null(device.NextSyncAt);
        await _unitOfWork.Received(1).SaveChangesAsync();
    }

    [Theory]
    // Alone, or beside a device that is itself not collecting — a grant waiting on a reconnect
    // cannot be what keeps the member monitored.
    [InlineData(null)]
    [InlineData(ConnectionStatus.TokenExpired)]
    public async Task Suspend_RefusesTheOnlyCollectingDevice(ConnectionStatus? otherStatus)
    {
        var only = SeedConnection(isPrimary: true);
        List<DeviceConnection> connections = [only];
        if (otherStatus is { } status)
            connections.Add(SeedConnection(status: status));
        _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(_memberId).Returns(connections);

        var ex = await Assert.ThrowsAsync<DeviceConnectionException>(() =>
            CreateSut().SuspendAsync(_userId, _memberId, only.Id));

        Assert.Equal(DeviceConnectionException.LastActiveDevice, ex.Code);
        Assert.True(ex.IsConflict);
        Assert.Null(only.SuspendedAt);
        await _unitOfWork.DidNotReceive().SaveChangesAsync();
    }

    [Fact]
    public async Task Suspend_RefusesTheLastCollectingDevice_WhenTheOthersAreSuspended()
    {
        var collecting = SeedConnection(isPrimary: true);
        var suspended = SeedConnection();
        suspended.SuspendedAt = DateTime.UtcNow.AddHours(-2);
        _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(_memberId).Returns([collecting, suspended]);

        var ex = await Assert.ThrowsAsync<DeviceConnectionException>(() =>
            CreateSut().SuspendAsync(_userId, _memberId, collecting.Id));

        Assert.Equal(DeviceConnectionException.LastActiveDevice, ex.Code);
    }

    [Fact]
    public async Task Suspend_Throws_ForViewOnlyCaregiver()
    {
        SetupCaregiverLink(isPrimaryCaregiver: false);
        var device = SeedConnection();
        _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(_memberId).Returns([device, SeedConnection(isPrimary: true)]);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            CreateSut().SuspendAsync(_userId, _memberId, device.Id));

        Assert.Null(device.SuspendedAt);
    }

    [Fact]
    public async Task Suspend_IsIdempotent()
    {
        var suspendedAt = DateTime.UtcNow.AddHours(-3);
        var device = SeedConnection();
        device.SuspendedAt = suspendedAt;
        _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(_memberId).Returns([device]);

        var response = await CreateSut().SuspendAsync(_userId, _memberId, device.Id);

        Assert.Equal(suspendedAt, device.SuspendedAt);
        Assert.Equal("suspended", response.Status);
        await _unitOfWork.DidNotReceive().SaveChangesAsync();
    }

    [Fact]
    public async Task Resume_ClearsTheSuspension_AndLeavesAnotherPrimaryInPlace()
    {
        var primary = SeedConnection(isPrimary: true);
        var suspended = SeedConnection();
        suspended.SuspendedAt = DateTime.UtcNow.AddDays(-2);
        suspended.SuspendedByUserId = _userId;
        _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(_memberId).Returns([primary, suspended]);

        var device = await CreateSut().ResumeAsync(_userId, _memberId, suspended.Id);

        Assert.Null(suspended.SuspendedAt);
        Assert.Null(suspended.SuspendedByUserId);
        Assert.False(suspended.IsPrimary);
        Assert.True(primary.IsPrimary);
        Assert.Equal("active", device.Status);
    }

    [Fact]
    public async Task Resume_TakesThePrimaryFlag_WhenNoOtherDeviceHoldsIt()
    {
        var suspended = SeedConnection();
        suspended.SuspendedAt = DateTime.UtcNow.AddDays(-2);
        _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(_memberId).Returns([suspended]);

        await CreateSut().ResumeAsync(_userId, _memberId, suspended.Id);

        Assert.True(suspended.IsPrimary);
    }

    [Fact]
    public async Task SetPrimary_RefusesASuspendedDevice()
    {
        var primary = SeedConnection(isPrimary: true);
        var suspended = SeedConnection();
        suspended.SuspendedAt = DateTime.UtcNow;
        _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(_memberId).Returns([primary, suspended]);

        var ex = await Assert.ThrowsAsync<DeviceConnectionException>(() =>
            CreateSut().SetPrimaryAsync(_userId, _memberId, suspended.Id));

        Assert.Equal(DeviceConnectionException.DeviceSuspended, ex.Code);
        Assert.True(primary.IsPrimary);
    }

    [Fact]
    public async Task GetDevices_ReportsASuspendedDevice_AsSuspended_WhateverItsGrantState()
    {
        var suspended = SeedConnection(status: ConnectionStatus.TokenExpired);
        suspended.SuspendedAt = DateTime.UtcNow.AddHours(-1);
        _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(_memberId).Returns([suspended]);

        var device = Assert.Single((await CreateSut().GetDevicesAsync(_userId, _memberId)).Devices);

        Assert.Equal("suspended", device.Status);
        Assert.Equal(suspended.SuspendedAt, device.SuspendedAt);
        Assert.Null(device.NextSyncAt);
    }

    // Copilot review on #1290: the rules over a member's devices (one primary, one connection per
    // account, never the last collecting device suspended) are each a read of the whole set then a
    // write, so every change takes the member's device lock before it reads.

    public static TheoryData<string> DeviceSetChanges =>
        ["connect", "disconnect", "primary", "suspend", "resume"];

    [Theory]
    [MemberData(nameof(DeviceSetChanges))]
    public async Task EveryDeviceSetChange_LocksTheMembersDevices_BeforeReadingThem(string change)
    {
        var primary = SeedAccount("ACCOUNT_A", isPrimary: true);
        var other = SeedAccount("ACCOUNT_B");
        if (change == "resume")
            other.SuspendedAt = DateTime.UtcNow;
        _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(_memberId).Returns([primary, other]);
        GrantIsForAccount("ACCOUNT_C");
        GrantReturns();
        var sut = CreateSut();

        // Initiation reads the member's devices too, but changes nothing, so it takes no lock;
        // only what follows it is under test.
        var initiation = change == "connect"
            ? await sut.InitiateConnectionAsync(_userId, _memberId, FitbitRequest())
            : null;
        _unitOfWork.DeviceConnections.ClearReceivedCalls();

        switch (change)
        {
            case "connect":
                await sut.CompleteConnectionAsync(_userId, "fitbit", new OAuthCallbackRequest
                {
                    Code = "code",
                    State = initiation!.State,
                    CodeVerifier = initiation.CodeVerifier,
                });
                break;
            case "disconnect":
                await sut.DisconnectAsync(_userId, _memberId, other.Id);
                break;
            case "primary":
                await sut.SetPrimaryAsync(_userId, _memberId, other.Id);
                break;
            case "suspend":
                await sut.SuspendAsync(_userId, _memberId, primary.Id);
                break;
            case "resume":
                await sut.ResumeAsync(_userId, _memberId, other.Id);
                break;
        }

        Received.InOrder(() =>
        {
            _unitOfWork.BeginTransactionAsync();
            _unitOfWork.DeviceConnections.LockMemberDevicesAsync(_memberId, Arg.Any<CancellationToken>());
            _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(_memberId);
            _unitOfWork.SaveChangesAsync();
            _unitOfWork.CommitTransactionAsync();
        });
    }

    [Fact]
    public async Task CompleteConnection_Reconnect_ClearsTheStoredAccount_WhenTheNewOneCannotBeRead()
    {
        // The grant may be for another account; keeping the old id would label it with that account
        // for good, since the sync only captures an id that is missing.
        var existing = SeedAccount("ACCOUNT_A", status: ConnectionStatus.TokenExpired);
        _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(_memberId).Returns([existing]);
        GrantIsForAccount(null);
        GrantReturns(access: "new_access");

        await ConnectAsync(CreateSut(), FitbitRequest(ConnectDeviceRequest.ModeReconnect, existing.Id));

        Assert.Null(existing.HealthUserId);
        Assert.Equal("enc(new_access)", existing.AccessToken);
    }

    [Fact]
    public async Task CompleteConnection_Reconnect_ClearsTheStoredProviderUserId_WhenTheNewGrantReportsNone()
    {
        // Left in place, the old id would keep matching later grants against an account this
        // connection may no longer be on.
        var existing = SeedConnection(status: ConnectionStatus.TokenExpired);
        existing.Metadata = """{"providerUserId":"ACCOUNT_A"}""";
        _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(_memberId).Returns([existing]);
        GrantIsForAccount(null);
        GrantReturns(access: "new_access", providerUserId: null);

        await ConnectAsync(CreateSut(), FitbitRequest(ConnectDeviceRequest.ModeReconnect, existing.Id));

        Assert.Null(existing.Metadata);
    }

    // Copilot review round 4 on #1290.

    [Fact]
    public async Task CompleteConnection_Reconnect_DropsTheStoredRefreshToken_WhenTheAccountCannotBeConfirmed()
    {
        // The new access token may be another account's; paired with the old account's refresh
        // token, the next expiry would switch the card back to that account.
        var existing = SeedAccount("ACCOUNT_A");
        _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(_memberId).Returns([existing]);
        GrantIsForAccount(null);
        GrantReturns(access: "new_access", refresh: null);

        await ConnectAsync(CreateSut(), FitbitRequest(ConnectDeviceRequest.ModeReconnect, existing.Id));

        Assert.Equal("enc(new_access)", existing.AccessToken);
        Assert.Null(existing.RefreshToken);
    }

    [Fact]
    public async Task Disconnect_OfTheLastCollectingDevice_LeavesASuspendedSiblingAsPrimary()
    {
        // Suspension only needs another device collecting at the time; that one can be removed
        // later. The member keeps a primary rather than none.
        var collecting = SeedAccount("ACCOUNT_A", isPrimary: true);
        var suspended = SeedAccount("ACCOUNT_B");
        suspended.SuspendedAt = DateTime.UtcNow.AddDays(-1);
        _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(_memberId).Returns([collecting, suspended]);

        await CreateSut().DisconnectAsync(_userId, _memberId, collecting.Id);

        Assert.True(suspended.IsPrimary);
        Assert.NotNull(suspended.SuspendedAt);
    }

    [Fact]
    public async Task CompleteConnection_Add_DoesNotMatch_OnOneIdentifierWhenTheOtherConflicts()
    {
        // A row carrying a stale provider user id beside a current health-user id must not catch a
        // grant for another account that happens to share the stale one.
        var existing = SeedAccount("ACCOUNT_A", isPrimary: true);
        existing.Metadata = """{"providerUserId":"STALE"}""";
        _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(_memberId).Returns([existing]);
        GrantIsForAccount("ACCOUNT_B");
        GrantReturns(providerUserId: "STALE");

        var device = await ConnectAsync(CreateSut(), FitbitRequest());

        await _unitOfWork.DeviceConnections.Received(1).AddAsync(Arg.Any<DeviceConnection>());
        Assert.False(device.AlreadyConnected);
        Assert.Equal("enc(access)", existing.AccessToken);
    }

    [Fact]
    public async Task Suspend_RollsBack_WhenItIsRefused()
    {
        var only = SeedConnection(isPrimary: true);
        _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(_memberId).Returns([only]);

        await Assert.ThrowsAsync<DeviceConnectionException>(() =>
            CreateSut().SuspendAsync(_userId, _memberId, only.Id));

        await _unitOfWork.Received(1).RollbackTransactionAsync();
        await _unitOfWork.DidNotReceive().CommitTransactionAsync();
    }

    // Revocation is queued, never made in the request: the entry is written in the same save that
    // discards the tokens, and the Worker ends the grant (see GrantRevocationServiceTests for the
    // shared-grant check it makes first).

    private Task QueuedRevocation(Func<PendingGrantRevocation, bool> match) =>
        _unitOfWork.PendingGrantRevocations.Received(1).AddAsync(Arg.Is<PendingGrantRevocation>(r => match(r)));

    private Task NothingQueued() =>
        _unitOfWork.PendingGrantRevocations.DidNotReceiveWithAnyArgs().AddAsync(default!);

    [Fact]
    public async Task Disconnect_QueuesTheGrant_WithTheTokenItHeld_InTheSameSave()
    {
        var connection = SeedAccount("ACCOUNT_A", isPrimary: true);
        _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(_memberId).Returns([connection]);

        await CreateSut().DisconnectAsync(_userId, _memberId, connection.Id);

        await QueuedRevocation(r =>
            r.DeviceConnectionId == connection.Id
            && r.Token == "enc(refresh)"
            && r.HealthUserId == "ACCOUNT_A"
            && r.CardiMemberId == _memberId
            && r.DeviceType == DeviceType.Fitbit);
        Assert.Null(connection.RefreshToken);
        Received.InOrder(() =>
        {
            _unitOfWork.PendingGrantRevocations.AddAsync(Arg.Any<PendingGrantRevocation>());
            _unitOfWork.SaveChangesAsync();
            _unitOfWork.CommitTransactionAsync();
        });
    }

    [Fact]
    public async Task Disconnect_QueuesNothing_ForAConnectionHoldingNoToken()
    {
        var connection = SeedConnection(isPrimary: true);
        connection.AccessToken = null;
        connection.RefreshToken = null;
        _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(_memberId).Returns([connection]);

        await CreateSut().DisconnectAsync(_userId, _memberId, connection.Id);

        await NothingQueued();
    }

    [Fact]
    public async Task CompleteConnection_Replace_StoresTheNewDevice_RetiresTheOld_AndQueuesItsGrant()
    {
        var old = SeedAccount("ACCOUNT_A", isPrimary: true);
        old.RefreshToken = "enc(old_refresh)";
        _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(_memberId).Returns([old]);
        GrantIsForAccount("ACCOUNT_B");
        GrantReturns(access: "new_access", refresh: "new_refresh");
        DeviceConnection? added = null;
        await _unitOfWork.DeviceConnections.AddAsync(Arg.Do<DeviceConnection>(c => added = c));

        var device = await ConnectAsync(CreateSut(), FitbitRequest(ConnectDeviceRequest.ModeReplace, old.Id));

        Assert.NotNull(added);
        Assert.True(added!.IsPrimary);
        Assert.Equal("ACCOUNT_B", added.HealthUserId);
        Assert.False(old.IsActive);
        Assert.False(old.IsPrimary);
        Assert.Equal(ConnectionStatus.Disconnected, old.ConnectionStatus);
        Assert.Null(old.RefreshToken);
        Assert.Equal(old.Id, device.ReplacedDeviceId);
        // The new device, the old one's removal and its queued revocation all land in the one save.
        await _unitOfWork.Received(1).SaveChangesAsync();
        await QueuedRevocation(r => r.DeviceConnectionId == old.Id && r.Token == "enc(old_refresh)");
    }

    [Fact]
    public async Task CompleteConnection_Replace_OnTheReplacedDevicesOwnAccount_JustReconnectsIt_AndQueuesNothing()
    {
        var old = SeedAccount("ACCOUNT_A", isPrimary: true, status: ConnectionStatus.TokenExpired);
        _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(_memberId).Returns([old]);
        GrantIsForAccount("ACCOUNT_A");
        GrantReturns(access: "new_access");

        var device = await ConnectAsync(CreateSut(), FitbitRequest(ConnectDeviceRequest.ModeReplace, old.Id));

        await _unitOfWork.DeviceConnections.DidNotReceive().AddAsync(Arg.Any<DeviceConnection>());
        Assert.True(old.IsActive);
        Assert.Equal(ConnectionStatus.Connected, old.ConnectionStatus);
        Assert.Null(device.ReplacedDeviceId);
        await NothingQueued();
    }

    [Fact]
    public async Task CompleteConnection_RefusedReconnect_QueuesTheGrantItCouldNotStore()
    {
        var existing = SeedAccount("ACCOUNT_A", status: ConnectionStatus.TokenExpired);
        _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(_memberId).Returns([existing]);
        GrantIsForAccount("ACCOUNT_B");
        GrantReturns(access: "b_access", refresh: "b_refresh");

        var ex = await Assert.ThrowsAsync<DeviceConnectionException>(() =>
            ConnectAsync(CreateSut(), FitbitRequest(ConnectDeviceRequest.ModeReconnect, existing.Id)));

        Assert.Equal(DeviceConnectionException.DifferentAccount, ex.Code);
        await _unitOfWork.Received(1).RollbackTransactionAsync();
        // The rolled-back writes must not ride along with the queued entry.
        _unitOfWork.Received(1).ClearTracking();
        await QueuedRevocation(r =>
            r.HealthUserId == "ACCOUNT_B" && r.Token == "enc(b_refresh)" && r.DeviceConnectionId != existing.Id);
        await _unitOfWork.Received(1).SaveChangesAsync();
    }

    [Fact]
    public async Task CompleteConnection_RefusedGrant_ForAnAccountThatCannotBeRead_QueuesNothing()
    {
        // An unknown account may be one a live connection on another member reads through, and
        // revocation is grant-wide.
        _unitOfWork.DeviceConnections.LockMemberDevicesAsync(_memberId, Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("db down"));
        GrantIsForAccount(null);
        GrantReturns();

        await Assert.ThrowsAsync<InvalidOperationException>(() => ConnectAsync(CreateSut(), FitbitRequest()));

        await NothingQueued();
    }

    [Fact]
    public async Task CompleteConnection_CancelledAfterTheExchange_StillQueuesTheUnstoredGrant()
    {
        GrantIsForAccount("ACCOUNT_B");
        GrantReturns(access: "b_access", refresh: "b_refresh");
        _unitOfWork.DeviceConnections.LockMemberDevicesAsync(_memberId, Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new OperationCanceledException());

        await Assert.ThrowsAsync<OperationCanceledException>(() => ConnectAsync(CreateSut(), FitbitRequest()));

        await QueuedRevocation(r => r.Token == "enc(b_refresh)");
    }

    [Fact]
    public async Task CompleteConnection_WhenTheTransactionCannotOpen_StillQueuesTheUnstoredGrant()
    {
        GrantIsForAccount("ACCOUNT_B");
        GrantReturns(access: "b_access", refresh: "b_refresh");
        _unitOfWork.BeginTransactionAsync().Returns<Task>(_ => throw new InvalidOperationException("db down"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => ConnectAsync(CreateSut(), FitbitRequest()));

        await QueuedRevocation(r => r.Token == "enc(b_refresh)");
    }

    [Fact]
    public async Task CompleteConnection_RefusedReconnect_ReportsTheRefusal_EvenWhenQueuingFails()
    {
        var existing = SeedAccount("ACCOUNT_A", status: ConnectionStatus.TokenExpired);
        _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(_memberId).Returns([existing]);
        GrantIsForAccount("ACCOUNT_B");
        GrantReturns();
        _unitOfWork.PendingGrantRevocations.AddAsync(Arg.Any<PendingGrantRevocation>())
            .Returns<Task>(_ => throw new InvalidOperationException("db down"));

        var ex = await Assert.ThrowsAsync<DeviceConnectionException>(() =>
            ConnectAsync(CreateSut(), FitbitRequest(ConnectDeviceRequest.ModeReconnect, existing.Id)));

        Assert.Equal(DeviceConnectionException.DifferentAccount, ex.Code);
    }

    [Fact]
    public async Task CompleteConnection_CancelledDuringTheIdentityLookup_RollsBack_AndQueuesNothing()
    {
        // Reaches the cleanup path; with the account unknown nothing is queued, since an unknown
        // account may be one a live connection reads through.
        _accountIdentity.TryResolveAsync(Arg.Any<DeviceType>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<string?>>(_ => throw new OperationCanceledException());
        GrantReturns();

        await Assert.ThrowsAsync<OperationCanceledException>(() => ConnectAsync(CreateSut(), FitbitRequest()));

        await _unitOfWork.Received(1).RollbackTransactionAsync();
        await NothingQueued();
    }
}
