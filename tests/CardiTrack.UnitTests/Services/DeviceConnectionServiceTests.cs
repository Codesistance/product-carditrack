using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.Exceptions;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.ExternalClients;
using CardiTrack.Infrastructure.Security;
using CardiTrack.Infrastructure.Services;
using CardiTrack.Infrastructure.Settings;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
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
    public async Task GetAppRedirectUri_ReturnsNull_ForUnknownStateOrProviderMismatch()
    {
        var sut = CreateSut();
        var initiation = await sut.InitiateConnectionAsync(_userId, _memberId, FitbitRequest());

        Assert.Null(await sut.GetAppRedirectUriAsync("fitbit", "not-a-real-state"));
        Assert.Null(await sut.GetAppRedirectUriAsync("garmin", initiation.State));
        Assert.Null(await sut.GetAppRedirectUriAsync("not_a_provider", initiation.State));
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
}
