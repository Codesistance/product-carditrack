using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.ExternalClients;
using CardiTrack.Infrastructure.Services;
using CardiTrack.Infrastructure.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// Pins the auth self-heal: a connection the provider refused is retried on a widening backoff,
/// returned to service the moment a refresh works, and left for re-consent when it never does.
/// </summary>
public class DeviceAuthRecoveryServiceTests
{
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IDeviceConnectionRepository _connections = Substitute.For<IDeviceConnectionRepository>();
    private readonly IOAuthTokenRefreshService _tokenRefresh = Substitute.For<IOAuthTokenRefreshService>();
    private readonly INotificationGapResolver _gapResolver = Substitute.For<INotificationGapResolver>();

    private static readonly DateTime UtcNow = new(2026, 8, 13, 12, 0, 0, DateTimeKind.Utc);

    private readonly Guid _memberId = Guid.NewGuid();
    private readonly Guid _connectionId = Guid.NewGuid();

    public DeviceAuthRecoveryServiceTests()
    {
        _unitOfWork.DeviceConnections.Returns(_connections);
        _connections.GetDueForAuthRecoveryAsync(Arg.Any<DateTime>()).Returns([Connection()]);
    }

    private DeviceConnection Connection(int attempts = 0) => new()
    {
        Id = _connectionId,
        CardiMemberId = _memberId,
        DeviceType = DeviceType.Fitbit,
        ConnectionStatus = ConnectionStatus.TokenExpired,
        RefreshToken = "encrypted",
        AuthRecoveryAttempts = attempts,
        IsActive = true,
    };

    private DeviceAuthRecoveryService CreateSut() =>
        new(_unitOfWork, _tokenRefresh, _gapResolver,
            Options.Create<List<DeviceProviderSettings>>(
            [
                new DeviceProviderSettings
                {
                    Provider = "GoogleHealth",
                    DeviceTypes = [nameof(DeviceType.Fitbit)],
                },
            ]),
            NullLogger<DeviceAuthRecoveryService>.Instance);

    [Fact]
    public async Task ARefreshThatWorks_ReturnsTheConnectionToService()
    {
        _tokenRefresh
            .RefreshIfExpiredAsync(Arg.Any<DeviceConnection>(), Arg.Any<DeviceProviderSettings>())
            .Returns("fresh-access-token");

        var recovered = await CreateSut().RecoverDueConnectionsAsync(UtcNow);

        Assert.Equal(1, recovered);
        await _connections.Received(1).MarkAuthRecoveredAsync(_connectionId);
        await _connections.DidNotReceive().MarkAuthRecoveryFailedAsync(
            Arg.Any<Guid>(), Arg.Any<DateTime>());
    }

    /// <summary>
    /// The caregiver is told to reconnect while the connection is broken, so a connection that
    /// fixes itself has to clear that ask immediately — not at tomorrow's rule run.
    /// </summary>
    [Fact]
    public async Task ARecoveredConnection_ClearsTheReconnectNudge()
    {
        _tokenRefresh
            .RefreshIfExpiredAsync(Arg.Any<DeviceConnection>(), Arg.Any<DeviceProviderSettings>())
            .Returns("fresh-access-token");

        await CreateSut().RecoverDueConnectionsAsync(UtcNow);

        await _gapResolver.Received(1).ResolveForCardiMemberAsync(_memberId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ARefusedRefresh_SchedulesTheNextAttempt_AndLeavesTheConnectionBroken()
    {
        _tokenRefresh
            .RefreshIfExpiredAsync(Arg.Any<DeviceConnection>(), Arg.Any<DeviceProviderSettings>())
            .ThrowsAsync(new InvalidOperationException("invalid_grant"));

        var recovered = await CreateSut().RecoverDueConnectionsAsync(UtcNow);

        Assert.Equal(0, recovered);
        await _connections.Received(1).MarkAuthRecoveryFailedAsync(
            _connectionId, UtcNow.AddMinutes(15));
        await _connections.DidNotReceive().MarkAuthRecoveredAsync(Arg.Any<Guid>());
        await _gapResolver.DidNotReceive().ResolveForCardiMemberAsync(
            Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A grant nobody is coming back for must cost the provider one request a day, not ninety-six
    /// — and must keep being retried, because the wearer may re-grant access at any time.
    /// </summary>
    [Theory]
    [InlineData(0, 15)]        // first failure → quarter hour
    [InlineData(1, 60)]        // then an hour
    [InlineData(2, 360)]       // then six
    [InlineData(3, 1440)]      // then a day
    [InlineData(9, 1440)]      // and a day thereafter, forever
    public async Task TheBackoffWidens_ThenRestsAtDaily(int failuresSoFar, int expectedMinutes)
    {
        _connections.GetDueForAuthRecoveryAsync(Arg.Any<DateTime>()).Returns([Connection(failuresSoFar)]);
        _tokenRefresh
            .RefreshIfExpiredAsync(Arg.Any<DeviceConnection>(), Arg.Any<DeviceProviderSettings>())
            .ThrowsAsync(new InvalidOperationException("invalid_grant"));

        await CreateSut().RecoverDueConnectionsAsync(UtcNow);

        await _connections.Received(1).MarkAuthRecoveryFailedAsync(
            _connectionId, UtcNow.AddMinutes(expectedMinutes));
    }

    /// <summary>
    /// A device type no provider config claims cannot be recovered by retrying, but it must not
    /// spin at full rate either — it is a deployment fault, and it backs off like the rest.
    /// </summary>
    [Fact]
    public async Task AnUnclaimedDeviceType_IsBackedOffRatherThanRetriedHard()
    {
        _connections.GetDueForAuthRecoveryAsync(Arg.Any<DateTime>()).Returns(
        [
            new DeviceConnection
            {
                Id = _connectionId,
                CardiMemberId = _memberId,
                DeviceType = DeviceType.Withings,
                ConnectionStatus = ConnectionStatus.AuthError,
                RefreshToken = "encrypted",
                IsActive = true,
            },
        ]);

        var recovered = await CreateSut().RecoverDueConnectionsAsync(UtcNow);

        Assert.Equal(0, recovered);
        await _tokenRefresh.DidNotReceive().RefreshIfExpiredAsync(
            Arg.Any<DeviceConnection>(), Arg.Any<DeviceProviderSettings>());
        await _connections.Received(1).MarkAuthRecoveryFailedAsync(_connectionId, UtcNow.AddMinutes(15));
    }

    /// <summary>One connection's failure must not cost the rest of the fleet its pass.</summary>
    [Fact]
    public async Task OneConnectionsFailure_DoesNotStopTheOthers()
    {
        var secondId = Guid.NewGuid();
        _connections.GetDueForAuthRecoveryAsync(Arg.Any<DateTime>()).Returns(
        [
            Connection(),
            new DeviceConnection
            {
                Id = secondId,
                CardiMemberId = Guid.NewGuid(),
                DeviceType = DeviceType.Fitbit,
                ConnectionStatus = ConnectionStatus.TokenExpired,
                RefreshToken = "encrypted",
                IsActive = true,
            },
        ]);

        _tokenRefresh
            .RefreshIfExpiredAsync(
                Arg.Is<DeviceConnection>(c => c.Id == _connectionId), Arg.Any<DeviceProviderSettings>())
            .ThrowsAsync(new InvalidOperationException("boom"));
        _tokenRefresh
            .RefreshIfExpiredAsync(
                Arg.Is<DeviceConnection>(c => c.Id == secondId), Arg.Any<DeviceProviderSettings>())
            .Returns("fresh-access-token");

        var recovered = await CreateSut().RecoverDueConnectionsAsync(UtcNow);

        Assert.Equal(1, recovered);
        await _connections.Received(1).MarkAuthRecoveredAsync(secondId);
    }
}
