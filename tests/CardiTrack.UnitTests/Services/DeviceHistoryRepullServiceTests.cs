using CardiTrack.Application.Exceptions;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Services;
using CardiTrack.Infrastructure.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// A re-pull spends a wearer's provider quota on a caregiver's say-so, so these pin the gates
/// between the tap and the work order: access, the monitoring pause, the connection's state,
/// one-open-at-a-time, and the cooldown — and that a request that passes them is recorded
/// exactly as asked.
/// </summary>
public class DeviceHistoryRepullServiceTests
{
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly ICardiMemberRepository _members = Substitute.For<ICardiMemberRepository>();
    private readonly IDeviceConnectionRepository _connections = Substitute.For<IDeviceConnectionRepository>();
    private readonly IDeviceHistoryRepullRepository _repulls = Substitute.For<IDeviceHistoryRepullRepository>();
    private readonly ICardiMemberAccessService _access = Substitute.For<ICardiMemberAccessService>();

    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _memberId = Guid.NewGuid();
    private readonly DeviceConnection _connection;

    public DeviceHistoryRepullServiceTests()
    {
        _unitOfWork.CardiMembers.Returns(_members);
        _unitOfWork.DeviceConnections.Returns(_connections);
        _unitOfWork.DeviceHistoryRepulls.Returns(_repulls);

        _members.GetByIdAsync(_memberId).Returns(new CardiMember
        {
            Id = _memberId,
            Name = "Margaret Doe",
            IsActive = true,
        });

        _connection = new DeviceConnection
        {
            Id = Guid.NewGuid(),
            CardiMemberId = _memberId,
            DeviceType = DeviceType.Fitbit,
            ConnectionStatus = ConnectionStatus.Connected,
            IsActive = true,
        };
        _connections.GetByCardiMemberIdAsync(_memberId).Returns([_connection]);

        _repulls.GetOpenByConnectionIdAsync(_connection.Id, Arg.Any<CancellationToken>())
            .Returns((DeviceHistoryRepull?)null);
        _repulls.GetLastCompletedAtAsync(_connection.Id, Arg.Any<CancellationToken>())
            .Returns((DateTime?)null);
    }

    private DeviceHistoryRepullService CreateSut(int cooldownHours = 48)
    {
        var settings = new DeviceProviderSettings
        {
            Provider = "GoogleHealth",
            DeviceTypes = ["Fitbit", "GooglePixelWatch"],
            HistoryRepullCooldownHours = cooldownHours,
        };
        return new DeviceHistoryRepullService(
            _unitOfWork,
            _access,
            Options.Create(new List<DeviceProviderSettings> { settings }),
            NullLogger<DeviceHistoryRepullService>.Instance);
    }

    private Task Request(int days = 30) =>
        CreateSut().RequestAsync(_userId, _memberId, _connection.Id, days);

    private async Task<HistoryRepullUnavailableException> RefusedWith(string code, Func<Task> act)
    {
        var ex = await Assert.ThrowsAsync<HistoryRepullUnavailableException>(act);
        Assert.Equal(code, ex.Code);
        return ex;
    }

    [Fact]
    public async Task Request_WhenAccessDenied_RecordsNothing()
    {
        _access.RequireViewAccessAsync(_userId, _memberId, Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new KeyNotFoundException("CardiMember not found")));

        await Assert.ThrowsAsync<KeyNotFoundException>(() => Request());

        await _repulls.DidNotReceiveWithAnyArgs().AddAsync(default!);
    }

    [Fact]
    public async Task Request_IsTheViewTier_NotManage()
    {
        // Any caregiver who can see the member may fill a gap they noticed; the cooldown is what
        // bounds the spend, not the tier.
        await Request();

        await _access.Received(1).RequireViewAccessAsync(_userId, _memberId, Arg.Any<CancellationToken>());
        await _access.DidNotReceiveWithAnyArgs().RequireManageAccessAsync(default, default, default);
    }

    [Fact]
    public async Task Request_ForADeviceThatIsNotTheMembers_Is404()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => CreateSut().RequestAsync(_userId, _memberId, Guid.NewGuid(), 30));
    }

    [Fact]
    public async Task Request_WhenMonitoringIsPaused_IsRefused()
    {
        _members.GetByIdAsync(_memberId).Returns(new CardiMember
        {
            Id = _memberId,
            IsActive = true,
            MonitoringPausedUntil = DateTime.UtcNow.AddHours(2),
        });

        await RefusedWith(HistoryRepullUnavailableException.MonitoringPaused, () => Request());
        await _repulls.DidNotReceiveWithAnyArgs().AddAsync(default!);
    }

    [Theory]
    [InlineData(ConnectionStatus.Disconnected)]
    [InlineData(ConnectionStatus.TokenExpired)]
    [InlineData(ConnectionStatus.AuthError)]
    public async Task Request_ForAConnectionWithNoUsableGrant_IsRefused(ConnectionStatus status)
    {
        _connection.ConnectionStatus = status;

        await RefusedWith(HistoryRepullUnavailableException.DeviceNotSyncable, () => Request());
    }

    [Fact]
    public async Task Request_ForARemovedConnection_IsRefused()
    {
        _connection.IsActive = false;

        await RefusedWith(HistoryRepullUnavailableException.DeviceNotSyncable, () => Request());
    }

    [Fact]
    public async Task Request_ForAConnectionInSyncError_IsAccepted()
    {
        // The routine sync still pulls a SyncError connection — last time's hiccup is exactly
        // the kind of gap a re-pull exists to fill.
        _connection.ConnectionStatus = ConnectionStatus.SyncError;

        await Request();

        await _repulls.Received(1).AddAsync(Arg.Any<DeviceHistoryRepull>());
    }

    [Fact]
    public async Task Request_WhileOneIsOpen_IsRefused()
    {
        _repulls.GetOpenByConnectionIdAsync(_connection.Id, Arg.Any<CancellationToken>())
            .Returns(new DeviceHistoryRepull { Status = HistoryRepullStatus.InProgress });

        await RefusedWith(HistoryRepullUnavailableException.RepullInProgress, () => Request());
    }

    [Fact]
    public async Task Request_InsideTheCooldown_IsRefusedWithTooSoon()
    {
        _repulls.GetLastCompletedAtAsync(_connection.Id, Arg.Any<CancellationToken>())
            .Returns(DateTime.UtcNow.AddHours(-2));

        var ex = await RefusedWith(HistoryRepullUnavailableException.TooSoon, () => Request());
        Assert.Contains("hours", ex.Message);
    }

    [Fact]
    public async Task Request_AfterTheCooldown_IsAccepted()
    {
        _repulls.GetLastCompletedAtAsync(_connection.Id, Arg.Any<CancellationToken>())
            .Returns(DateTime.UtcNow.AddHours(-49));

        await Request();

        await _repulls.Received(1).AddAsync(Arg.Any<DeviceHistoryRepull>());
    }

    [Fact]
    public async Task Request_WithTheCooldownDisabled_NeverAsksWhenTheLastOneFinished()
    {
        _repulls.GetLastCompletedAtAsync(_connection.Id, Arg.Any<CancellationToken>())
            .Returns(DateTime.UtcNow.AddMinutes(-5));

        await CreateSut(cooldownHours: 0).RequestAsync(_userId, _memberId, _connection.Id, 30);

        await _repulls.DidNotReceiveWithAnyArgs().GetLastCompletedAtAsync(default, default);
        await _repulls.Received(1).AddAsync(Arg.Any<DeviceHistoryRepull>());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(91)]
    public async Task Request_WithDaysOutOfRange_IsRefusedBeforeAnythingIsWritten(int days)
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Request(days));

        await _repulls.DidNotReceiveWithAnyArgs().AddAsync(default!);
    }

    [Fact]
    public async Task Request_RecordsTheRangeEndingYesterday_AsPending_ByTheCaller()
    {
        DeviceHistoryRepull? saved = null;
        await _repulls.AddAsync(Arg.Do<DeviceHistoryRepull>(r => saved = r));

        var response = await CreateSut().RequestAsync(_userId, _memberId, _connection.Id, 30);

        Assert.NotNull(saved);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        Assert.Equal(today.AddDays(-1), saved.ToDate);
        Assert.Equal(today.AddDays(-30), saved.FromDate);
        Assert.Equal(HistoryRepullStatus.Pending, saved.Status);
        Assert.Equal(_connection.Id, saved.DeviceConnectionId);
        Assert.Equal(_memberId, saved.CardiMemberId);
        Assert.Equal(_userId, saved.RequestedByUserId);
        Assert.Null(saved.CompletedTo);
        await _unitOfWork.Received(1).SaveChangesAsync();

        Assert.Equal("pending", response.Status);
        Assert.Equal(30, response.Days);
        Assert.Equal(0, response.DaysDone);
        Assert.Null(response.NextAllowedAt);
    }
}
