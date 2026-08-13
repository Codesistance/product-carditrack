using CardiTrack.Application.Exceptions;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Services;
using CardiTrack.Infrastructure.Settings;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// The dashboard's refresh button reaches a wearable provider on a caregiver's say-so
/// (issue #67), so these pin the gates that stand between a tap and someone's health data:
/// access, the monitoring pause, and the rate limit.
/// </summary>
public class ManualDeviceSyncServiceTests
{
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly ICardiMemberRepository _members = Substitute.For<ICardiMemberRepository>();
    private readonly IDeviceConnectionRepository _connections = Substitute.For<IDeviceConnectionRepository>();
    private readonly ICardiMemberAccessService _access = Substitute.For<ICardiMemberAccessService>();
    private readonly IDeviceSyncService _fitbitSync = Substitute.For<IDeviceSyncService>();
    private readonly IDistributedCache _cache =
        new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));

    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _memberId = Guid.NewGuid();

    public ManualDeviceSyncServiceTests()
    {
        _unitOfWork.CardiMembers.Returns(_members);
        _unitOfWork.DeviceConnections.Returns(_connections);

        _members.GetByIdAsync(_memberId).Returns(new CardiMember
        {
            Id = _memberId,
            Name = "Margaret Doe",
            IsActive = true,
        });
    }

    private ManualDeviceSyncService CreateSut()
    {
        // A real keyed container rather than a stub: resolving IDeviceSyncService through the
        // configured DeviceType→HealthApi mapping is the part of this service most likely to
        // break, and only real keyed registration plus real options exercise it.
        var services = new ServiceCollection();
        services.Configure<List<DeviceProviderSettings>>(list => list.Add(new DeviceProviderSettings
        {
            Provider = "GoogleHealth",
            DeviceTypes = ["Fitbit", "GooglePixelWatch"],
        }));
        services.AddKeyedSingleton<IDeviceSyncService>(HealthApi.GoogleHealth, _fitbitSync);

        return new ManualDeviceSyncService(
            _unitOfWork,
            _access,
            services.BuildServiceProvider(),
            _cache,
            Substitute.For<ILogger<ManualDeviceSyncService>>());
    }

    private DeviceConnection Connection(DeviceType type = DeviceType.Fitbit) => new()
    {
        Id = Guid.NewGuid(),
        CardiMemberId = _memberId,
        DeviceType = type,
        ConnectionStatus = ConnectionStatus.Connected,
        IsActive = true,
    };

    private void GivenConnections(params DeviceConnection[] connections) =>
        _connections.GetActiveByCardiMemberIdAsync(_memberId).Returns(connections.ToList());

    [Fact]
    public async Task SyncMemberAsync_WhenAccessDenied_DoesNotTouchTheProvider()
    {
        _access.RequireViewAccessAsync(_userId, _memberId, Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new KeyNotFoundException("CardiMember not found")));
        GivenConnections(Connection());

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => CreateSut().SyncMemberAsync(_userId, _memberId));

        await _fitbitSync.DidNotReceive().SyncCardiMemberAsync(Arg.Any<DeviceConnection>());
    }

    [Fact]
    public async Task SyncMemberAsync_WhenMemberInactive_Throws()
    {
        _members.GetByIdAsync(_memberId).Returns(new CardiMember { Id = _memberId, IsActive = false });

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => CreateSut().SyncMemberAsync(_userId, _memberId));
    }

    // A paused member is not being watched; collecting for them by a side door would make the
    // pause a lie. The scheduled pull already excludes them, so this path must too.
    [Fact]
    public async Task SyncMemberAsync_WhenMonitoringPaused_RefusesWithoutSyncing()
    {
        _members.GetByIdAsync(_memberId).Returns(new CardiMember
        {
            Id = _memberId,
            IsActive = true,
            MonitoringPausedUntil = DateTime.UtcNow.AddHours(2),
        });
        GivenConnections(Connection());

        var ex = await Assert.ThrowsAsync<ManualSyncUnavailableException>(
            () => CreateSut().SyncMemberAsync(_userId, _memberId));

        Assert.Equal(ManualSyncUnavailableException.MonitoringPaused, ex.Code);
        await _fitbitSync.DidNotReceive().SyncCardiMemberAsync(Arg.Any<DeviceConnection>());
    }

    [Fact]
    public async Task SyncMemberAsync_WithNoConnectedDevice_Refuses()
    {
        GivenConnections();

        var ex = await Assert.ThrowsAsync<ManualSyncUnavailableException>(
            () => CreateSut().SyncMemberAsync(_userId, _memberId));

        Assert.Equal(ManualSyncUnavailableException.NoConnectedDevice, ex.Code);
    }

    [Fact]
    public async Task SyncMemberAsync_SyncsEveryConnection_AndReportsTheNewestStamp()
    {
        var first = Connection();
        var second = Connection();
        GivenConnections(first, second);

        var stamp = new DateTime(2026, 8, 8, 12, 0, 0, DateTimeKind.Utc);
        // Stands in for UpdateLastSyncDateAsync: the service re-reads the connections rather than
        // trusting the clock, so the stamp has to arrive through the entities.
        _fitbitSync
            .When(s => s.SyncCardiMemberAsync(Arg.Any<DeviceConnection>()))
            .Do(call =>
            {
                var connection = call.Arg<DeviceConnection>();
                Assert.NotNull(connection);
                connection!.LastSyncDate = connection.Id == first.Id ? stamp : stamp.AddMinutes(-5);
            });

        var result = await CreateSut().SyncMemberAsync(_userId, _memberId);

        Assert.Equal(2, result.SyncedCount);
        Assert.Equal(0, result.FailedCount);
        Assert.Equal(stamp, result.LastSyncedAt);
        Assert.All(result.Devices, d => Assert.True(d.Succeeded));
    }

    // One provider being down is not a failed request — the other device's data still landed,
    // and the caller needs to see which one didn't.
    [Fact]
    public async Task SyncMemberAsync_WhenOneProviderFails_ReportsItWithoutSinkingTheRest()
    {
        var healthy = Connection();
        var broken = Connection();
        GivenConnections(healthy, broken);

        _fitbitSync
            .When(s => s.SyncCardiMemberAsync(broken))
            .Do(_ => throw new HttpRequestException("provider down"));

        var result = await CreateSut().SyncMemberAsync(_userId, _memberId);

        Assert.Equal(1, result.SyncedCount);
        Assert.Equal(1, result.FailedCount);
        Assert.NotNull(result.Devices.Single(d => d.DeviceId == broken.Id).Message);
        Assert.Null(result.Devices.Single(d => d.DeviceId == healthy.Id).Message);
    }

    // No registered provider is a gap in our wiring, not a reason to 500 at a caregiver.
    [Fact]
    public async Task SyncMemberAsync_WithNoSyncServiceForTheDeviceType_CountsItFailed()
    {
        GivenConnections(Connection(DeviceType.Garmin));

        var result = await CreateSut().SyncMemberAsync(_userId, _memberId);

        Assert.Equal(0, result.SyncedCount);
        Assert.Equal(1, result.FailedCount);
    }

    // A caller who gave up must not be reported a 200 with per-device failures, and the devices
    // behind the cancelled one must not keep pulling.
    [Fact]
    public async Task SyncMemberAsync_WhenCancelled_StopsInsteadOfReportingAFailedDevice()
    {
        var first = Connection();
        var second = Connection();
        GivenConnections(first, second);

        using var cts = new CancellationTokenSource();
        _fitbitSync
            .When(s => s.SyncCardiMemberAsync(first))
            .Do(_ =>
            {
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateSut().SyncMemberAsync(_userId, _memberId, cts.Token));

        await _fitbitSync.DidNotReceive().SyncCardiMemberAsync(second);
    }

    // A provider HTTP timeout also arrives as TaskCanceledException, but nobody asked us to stop —
    // that one is a provider failure and the other devices still deserve their turn.
    [Fact]
    public async Task SyncMemberAsync_WhenAProviderTimesOut_TreatsItAsAFailureNotACancellation()
    {
        var slow = Connection();
        var healthy = Connection();
        GivenConnections(slow, healthy);

        _fitbitSync
            .When(s => s.SyncCardiMemberAsync(slow))
            .Do(_ => throw new TaskCanceledException("The request timed out."));

        var result = await CreateSut().SyncMemberAsync(_userId, _memberId, CancellationToken.None);

        Assert.Equal(1, result.SyncedCount);
        Assert.Equal(1, result.FailedCount);
        await _fitbitSync.Received(1).SyncCardiMemberAsync(healthy);
    }

    // Provider quotas are per-app, so one caregiver leaning on refresh spends everyone's budget.
    [Fact]
    public async Task SyncMemberAsync_TwiceInsideTheCooldown_RefusesTheSecond()
    {
        GivenConnections(Connection());
        var sut = CreateSut();

        await sut.SyncMemberAsync(_userId, _memberId);
        var ex = await Assert.ThrowsAsync<ManualSyncUnavailableException>(
            () => sut.SyncMemberAsync(_userId, _memberId));

        Assert.Equal(ManualSyncUnavailableException.TooSoon, ex.Code);
        await _fitbitSync.Received(1).SyncCardiMemberAsync(Arg.Any<DeviceConnection>());
    }

    // The cooldown is per member: watching two people must not mean refreshing only one of them.
    [Fact]
    public async Task SyncMemberAsync_CooldownIsScopedToTheMember()
    {
        var otherMemberId = Guid.NewGuid();
        _members.GetByIdAsync(otherMemberId).Returns(new CardiMember { Id = otherMemberId, IsActive = true });
        _connections.GetActiveByCardiMemberIdAsync(otherMemberId)
            .Returns(new List<DeviceConnection> { Connection() });
        GivenConnections(Connection());
        var sut = CreateSut();

        await sut.SyncMemberAsync(_userId, _memberId);
        var result = await sut.SyncMemberAsync(_userId, otherMemberId);

        Assert.Equal(1, result.SyncedCount);
    }
}
