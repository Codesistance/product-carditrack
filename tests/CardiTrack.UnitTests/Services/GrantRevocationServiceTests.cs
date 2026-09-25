using CardiTrack.Application.Interfaces.Clients;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Services;
using CardiTrack.Infrastructure.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// The Worker's side of grant revocation: each queued grant is re-checked for sharing immediately
/// before the provider call — revocation ends the grant for the whole account — and retried on a
/// backoff until it is confirmed ended or given up on.
/// </summary>
public class GrantRevocationServiceTests
{
    private static readonly DateTime UtcNow = new(2026, 9, 25, 15, 0, 0, DateTimeKind.Utc);

    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IPendingGrantRevocationRepository _queue = Substitute.For<IPendingGrantRevocationRepository>();
    private readonly IDeviceConnectionRepository _connections = Substitute.For<IDeviceConnectionRepository>();
    private readonly IOAuthGrantRevoker _revoker = Substitute.For<IOAuthGrantRevoker>();
    private readonly Guid _memberId = Guid.NewGuid();

    public GrantRevocationServiceTests()
    {
        _unitOfWork.PendingGrantRevocations.Returns(_queue);
        _unitOfWork.DeviceConnections.Returns(_connections);
        _connections.GetByCardiMemberIdAsync(_memberId).Returns([]);
        _revoker.TryRevokeAsync(Arg.Any<DeviceConnection>(), Arg.Any<CancellationToken>()).Returns(true);
    }

    private GrantRevocationService CreateSut(string revocationUrl = "https://oauth2.googleapis.com/revoke") =>
        new(_unitOfWork, _revoker,
            Options.Create(new List<DeviceProviderSettings>
            {
                new()
                {
                    Provider = nameof(HealthApi.GoogleHealth),
                    DeviceTypes = ["Fitbit", "GooglePixelWatch"],
                    RevocationUrl = revocationUrl,
                },
            }),
            NullLogger<GrantRevocationService>.Instance);

    private PendingGrantRevocation Queue(string? account = "ACCOUNT_A", int attempts = 0)
    {
        var pending = new PendingGrantRevocation
        {
            Id = Guid.NewGuid(),
            CardiMemberId = _memberId,
            DeviceConnectionId = Guid.NewGuid(),
            DeviceType = DeviceType.Fitbit,
            HealthUserId = account,
            Token = "enc(refresh)",
            Attempts = attempts,
            NextAttemptAt = UtcNow,
        };
        _queue.GetDueAsync(UtcNow, Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([pending]);
        return pending;
    }

    private DeviceConnection Sibling(string? account, DeviceType type = DeviceType.GooglePixelWatch) => new()
    {
        Id = Guid.NewGuid(),
        CardiMemberId = _memberId,
        DeviceType = type,
        HealthUserId = account,
        IsActive = true,
        ConnectionStatus = ConnectionStatus.Connected,
    };

    [Fact]
    public async Task ADueGrant_IsRevoked_WithItsToken_AndDropped()
    {
        var pending = Queue();

        var ended = await CreateSut().RevokeDueAsync(UtcNow);

        Assert.Equal(1, ended);
        await _revoker.Received(1).TryRevokeAsync(
            Arg.Is<DeviceConnection>(c => c.Id == pending.DeviceConnectionId
                                          && c.RefreshToken == "enc(refresh)"
                                          && c.DeviceType == DeviceType.Fitbit),
            Arg.Any<CancellationToken>());
        _queue.Received(1).Remove(pending);
        await _unitOfWork.Received(1).SaveChangesAsync();
    }

    [Theory]
    // A sibling on the same API whose account is unknown, or known to be the same one, may read
    // through this grant.
    [InlineData("ACCOUNT_A", null)]
    [InlineData("ACCOUNT_A", "ACCOUNT_A")]
    // A grant whose own account is unknown may be any sibling's.
    [InlineData(null, "ACCOUNT_B")]
    public async Task AGrantASiblingMightShare_IsKept_AndDropped(string? grantAccount, string? siblingAccount)
    {
        var pending = Queue(grantAccount);
        _connections.GetByCardiMemberIdAsync(_memberId).Returns([Sibling(siblingAccount)]);

        var ended = await CreateSut().RevokeDueAsync(UtcNow);

        Assert.Equal(0, ended);
        await _revoker.DidNotReceiveWithAnyArgs().TryRevokeAsync(default!, default);
        _queue.Received(1).Remove(pending);
    }

    [Fact]
    public async Task AReplacedDevicesGrant_IsKept_WhenTheNewDevicesAccountIsUnknown()
    {
        // The new connection may be on the same account; revoking would take it down too.
        var pending = Queue("ACCOUNT_A");
        _connections.GetByCardiMemberIdAsync(_memberId).Returns([Sibling(null, DeviceType.Fitbit)]);

        await CreateSut().RevokeDueAsync(UtcNow);

        await _revoker.DidNotReceiveWithAnyArgs().TryRevokeAsync(default!, default);
        _queue.Received(1).Remove(pending);
    }

    [Fact]
    public async Task AGrantAnotherMembersConnectionReadsThrough_IsKept()
    {
        var pending = Queue("ACCOUNT_A");
        _connections.AnyOtherActiveWithHealthUserIdAsync(pending.DeviceConnectionId, "ACCOUNT_A").Returns(true);

        await CreateSut().RevokeDueAsync(UtcNow);

        await _revoker.DidNotReceiveWithAnyArgs().TryRevokeAsync(default!, default);
        _queue.Received(1).Remove(pending);
    }

    [Fact]
    public async Task AGrantWhoseSiblingsAreKnownToBeOtherAccounts_IsRevoked()
    {
        Queue("ACCOUNT_A");
        _connections.GetByCardiMemberIdAsync(_memberId).Returns([Sibling("ACCOUNT_B")]);

        Assert.Equal(1, await CreateSut().RevokeDueAsync(UtcNow));
    }

    [Fact]
    public async Task ASiblingOnAnotherApi_DoesNotShareTheGrant()
    {
        Queue("ACCOUNT_A");
        _connections.GetByCardiMemberIdAsync(_memberId).Returns([Sibling(null, DeviceType.Garmin)]);

        Assert.Equal(1, await CreateSut().RevokeDueAsync(UtcNow));
    }

    [Fact]
    public async Task AFailedAttempt_IsRetriedLater_OnAWideningBackoff()
    {
        var pending = Queue(attempts: 1);
        _revoker.TryRevokeAsync(Arg.Any<DeviceConnection>(), Arg.Any<CancellationToken>()).Returns(false);

        var ended = await CreateSut().RevokeDueAsync(UtcNow);

        Assert.Equal(0, ended);
        Assert.Equal(2, pending.Attempts);
        Assert.Equal(UtcNow + GrantRevocationService.BackoffAfter(2), pending.NextAttemptAt);
        _queue.DidNotReceive().Remove(pending);
        _queue.Received(1).Update(pending);
    }

    [Fact]
    public async Task AProviderTimeout_IsAFailedAttempt_NotAFailedPass()
    {
        // HttpClient reports its own timeout as a cancellation; the pass itself was not cancelled.
        var pending = Queue();
        _revoker.TryRevokeAsync(Arg.Any<DeviceConnection>(), Arg.Any<CancellationToken>())
            .Returns<Task<bool>>(_ => throw new TaskCanceledException());

        await CreateSut().RevokeDueAsync(UtcNow);

        Assert.Equal(1, pending.Attempts);
        _queue.DidNotReceive().Remove(pending);
    }

    [Fact]
    public async Task TheLastAttempt_GivesUp_AndDropsTheToken()
    {
        // The token must not sit in the database indefinitely.
        var pending = Queue(attempts: GrantRevocationService.MaxAttempts - 1);
        _revoker.TryRevokeAsync(Arg.Any<DeviceConnection>(), Arg.Any<CancellationToken>()).Returns(false);

        await CreateSut().RevokeDueAsync(UtcNow);

        _queue.Received(1).Remove(pending);
    }

    [Fact]
    public async Task AProviderWithNoRevocationEndpoint_IsNotRetried()
    {
        var pending = Queue();
        _revoker.TryRevokeAsync(Arg.Any<DeviceConnection>(), Arg.Any<CancellationToken>()).Returns(false);

        await CreateSut(revocationUrl: "").RevokeDueAsync(UtcNow);

        _queue.Received(1).Remove(pending);
        Assert.Equal(0, pending.Attempts);
    }

    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 15)]
    [InlineData(3, 45)]
    [InlineData(7, 24 * 60)]
    public void TheBackoff_TriplesFromFiveMinutes_CappedAtADay(int attempts, int minutes)
    {
        Assert.Equal(TimeSpan.FromMinutes(minutes), GrantRevocationService.BackoffAfter(attempts));
    }
}
