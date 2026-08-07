using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.Exceptions;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.ExternalClients;
using CardiTrack.Infrastructure.Extensions;
using CardiTrack.Application.Interfaces.Security;
using CardiTrack.Infrastructure.Security;
using CardiTrack.Infrastructure.Services;
using CardiTrack.Infrastructure.Settings;
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
    private readonly IDistributedCache _cache =
        new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));

    public DeviceConnectionServiceTests()
    {
        _unitOfWork.UserCardiMembers.GetByUserIdAsync(_userId).Returns(
        [
            new UserCardiMember { UserId = _userId, CardiMemberId = _memberId, IsActive = true }
        ]);
        _unitOfWork.CardiMembers.GetByIdAsync(_memberId).Returns(
            new CardiMember { Id = _memberId, Name = "Dad", IsActive = true });
        _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(_memberId).Returns([]);
        _unitOfWork.Devices.GetByDeviceTypeAsync(DeviceType.Fitbit).Returns((Device?)null);
        _encryption.Encrypt(Arg.Any<string>()).Returns(c => $"enc({c.Arg<string>()})");
    }

    private DeviceConnectionService CreateSut(Action<DeviceProviderSettings>? configure = null)
    {
        var fitbit = new DeviceProviderSettings
        {
            Provider = "Fitbit",
            ClientId = "fitbit_client",
            ClientSecret = "secret",
            AuthorizationUrl = "https://www.fitbit.com/oauth2/authorize",
            TokenUrl = "https://api.fitbit.com/oauth2/token",
            Scopes = ["activity", "heartrate", "sleep"],
        };
        configure?.Invoke(fitbit);
        return new DeviceConnectionService(
            _unitOfWork,
            _encryption,
            _cache,
            _codeExchange,
            _tokenRefresh,
            Options.Create(new List<DeviceProviderSettings> { fitbit }));
    }

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
    public async Task CompleteConnection_ReusesExistingConnection_ForSameProvider()
    {
        var existing = new DeviceConnection
        {
            CardiMemberId = _memberId,
            DeviceType = DeviceType.Fitbit,
            DeviceName = "Fitbit",
            IsPrimary = true,
            ConnectionStatus = ConnectionStatus.TokenExpired,
        };
        _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(_memberId).Returns([existing]);
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
        // back without one — nulling the stored token would strand the next silent refresh.
        var existing = new DeviceConnection
        {
            CardiMemberId = _memberId,
            DeviceType = DeviceType.Fitbit,
            DeviceName = "Fitbit",
            IsActive = true,
            ConnectionStatus = ConnectionStatus.Connected,
            AccessToken = "enc(old_access)",
            RefreshToken = "enc(old_refresh)",
        };
        _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(_memberId).Returns([existing]);
        _codeExchange.ExchangeCodeAsync(Arg.Any<DeviceProviderSettings>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(new OAuthTokenResult("new_access", null, 3600, null, null));

        var sut = CreateSut();
        var initiation = await sut.InitiateConnectionAsync(_userId, _memberId, FitbitRequest());

        await sut.CompleteConnectionAsync(_userId, "fitbit", new OAuthCallbackRequest
        {
            Code = "code",
            State = initiation.State,
            CodeVerifier = initiation.CodeVerifier,
        });

        Assert.Equal("enc(new_access)", existing.AccessToken);
        Assert.Equal("enc(old_refresh)", existing.RefreshToken);
    }

    [Fact]
    public async Task CompleteConnection_DropsStoredRefreshToken_WhenReconnectingOnAnotherAccount()
    {
        // Keeping the old account's refresh token here would leave background syncs pulling a
        // stranger's health data under this member.
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

        var sut = CreateSut();
        var initiation = await sut.InitiateConnectionAsync(_userId, _memberId, FitbitRequest());

        await sut.CompleteConnectionAsync(_userId, "fitbit", new OAuthCallbackRequest
        {
            Code = "code",
            State = initiation.State,
            CodeVerifier = initiation.CodeVerifier,
        });

        Assert.Null(existing.RefreshToken);
    }

    [Fact]
    public async Task InitiateConnection_AddsFirstConsentParams_WhenNoRefreshTokenHeld()
    {
        var sut = CreateSut(ForcesConsent);

        var result = await sut.InitiateConnectionAsync(_userId, _memberId, FitbitRequest());

        Assert.Contains("&prompt=consent", result.AuthorizationUrl);
    }

    [Fact]
    public async Task InitiateConnection_OmitsFirstConsentParams_WhenRefreshTokenAlreadyHeld()
    {
        _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(_memberId).Returns(
        [
            new DeviceConnection
            {
                CardiMemberId = _memberId,
                DeviceType = DeviceType.Fitbit,
                DeviceName = "Fitbit",
                IsActive = true,
                ConnectionStatus = ConnectionStatus.Connected,
                RefreshToken = "enc(refresh)",
            }
        ]);

        var result = await CreateSut(ForcesConsent).InitiateConnectionAsync(_userId, _memberId, FitbitRequest());

        Assert.DoesNotContain("prompt=consent", result.AuthorizationUrl);
        // The unconditional params are unaffected.
        Assert.Contains("&access_type=offline", result.AuthorizationUrl);
    }

    [Theory]
    // Disconnected: the provider may well have revoked the token, so re-consent is how we
    // get a usable one back. Connected but tokenless: there is nothing to preserve.
    [InlineData(ConnectionStatus.Disconnected, "enc(refresh)")]
    [InlineData(ConnectionStatus.Connected, null)]
    public async Task InitiateConnection_AddsFirstConsentParams_WhenExistingConnectionCannotRefresh(
        ConnectionStatus status, string? refreshToken)
    {
        _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(_memberId).Returns(
        [
            new DeviceConnection
            {
                CardiMemberId = _memberId,
                DeviceType = DeviceType.Fitbit,
                DeviceName = "Fitbit",
                IsActive = true,
                ConnectionStatus = status,
                RefreshToken = refreshToken,
            }
        ]);

        var result = await CreateSut(ForcesConsent).InitiateConnectionAsync(_userId, _memberId, FitbitRequest());

        Assert.Contains("&prompt=consent", result.AuthorizationUrl);
    }

    private static void ForcesConsent(DeviceProviderSettings settings)
    {
        settings.AdditionalAuthorizationParams = new Dictionary<string, string> { ["access_type"] = "offline" };
        settings.FirstConsentAuthorizationParams = new Dictionary<string, string> { ["prompt"] = "consent" };
    }

    [Fact]
    public async Task GetAppRedirectUri_ReturnsDeepLink_WithoutConsumingState()
    {
        _codeExchange.ExchangeCodeAsync(Arg.Any<DeviceProviderSettings>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(new OAuthTokenResult("access", "refresh", 3600, null, null));

        var sut = CreateSut();
        var initiation = await sut.InitiateConnectionAsync(_userId, _memberId, FitbitRequest());

        var deepLink = await sut.GetAppRedirectUriAsync("fitbit", initiation.State);

        Assert.Equal("carditrack://oauth/callback", deepLink);

        // The peek must not consume the state — the app still completes the flow afterwards.
        await sut.CompleteConnectionAsync(_userId, "fitbit", new OAuthCallbackRequest
        {
            Code = "code",
            State = initiation.State,
            CodeVerifier = initiation.CodeVerifier,
        });
    }

    [Fact]
    public async Task GetAppRedirectUri_RejectsNonAppSchemeRedirect()
    {
        // An https redirect cached at initiation must not turn the anonymous bounce
        // endpoint into an open redirect leaking code+state.
        var sut = CreateSut();
        var initiation = await sut.InitiateConnectionAsync(_userId, _memberId, new ConnectDeviceRequest
        {
            Provider = "fitbit",
            RedirectUri = "https://attacker.example.com/collect",
        });

        Assert.Null(await sut.GetAppRedirectUriAsync("fitbit", initiation.State));
    }

    [Fact]
    public async Task GetAppRedirectUri_RejectsRedirectCarryingAFragment()
    {
        // The bounce appends the callback parameters to whatever comes back, so a '#' would
        // swallow them and the app would receive no state, code or error at all.
        var sut = CreateSut();
        var initiation = await sut.InitiateConnectionAsync(_userId, _memberId, new ConnectDeviceRequest
        {
            Provider = "fitbit",
            RedirectUri = "carditrack://oauth/callback#done",
        });

        Assert.Null(await sut.GetAppRedirectUriAsync("fitbit", initiation.State));
    }

    [Fact]
    public async Task GetAppRedirectUri_ReturnsNull_ForUnknownStateOrProviderMismatch()
    {
        var sut = CreateSut();
        var initiation = await sut.InitiateConnectionAsync(_userId, _memberId, FitbitRequest());

        Assert.Null(await sut.GetAppRedirectUriAsync("fitbit", "not-a-real-state"));
        Assert.Null(await sut.GetAppRedirectUriAsync("garmin", initiation.State));
        Assert.Null(await sut.GetAppRedirectUriAsync("not_a_provider", initiation.State));
    }

    [Fact]
    public void AddFitbitProvider_FailsFast_WhenFitbitIsNotFirstProvider()
    {
        var services = new ServiceCollection();
        services.AddFitbitProvider();
        services.Configure<List<DeviceProviderSettings>>(list =>
            list.Add(new DeviceProviderSettings { Provider = "Garmin" }));

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
}
