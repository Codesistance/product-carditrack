using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.PipelineJobs.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// Pins the drain contract: one sync per connection however many notifications arrived, the
/// paused/removed exclusion living in the lookup, and acknowledgment meaning "nothing here
/// still needs a retry" — never "we saw it".
/// </summary>
public class NotificationDrainServiceTests
{
    private readonly INotificationSource _source = Substitute.For<INotificationSource>();
    private readonly IDeviceConnectionRepository _connections = Substitute.For<IDeviceConnectionRepository>();
    private readonly IDeviceSyncService _sync = Substitute.For<IDeviceSyncService>();

    private readonly DeviceConnection _connection = new()
    {
        Id = Guid.NewGuid(),
        CardiMemberId = Guid.NewGuid(),
        DeviceType = DeviceType.Fitbit,
        HealthUserId = "abc-123",
    };

    private NotificationDrainService CreateSut()
    {
        // A real keyed container, because keyed resolution is part of the contract under test.
        var services = new ServiceCollection();
        services.AddKeyedSingleton(DeviceType.Fitbit, _sync);
        return new NotificationDrainService(
            _source, _connections, services.BuildServiceProvider(),
            NullLogger<NotificationDrainService>.Instance);
    }

    private void SetupOneBatch(params ReceivedNotification[] messages)
    {
        // A call counter, not Returns(first, subsequent): an empty collection expression there
        // binds as the empty params array and every pull would return the batch again.
        var pulls = 0;
        _source.PullAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ => Interlocked.Increment(ref pulls) == 1 ? messages : []);
    }

    private static ReceivedNotification Notification(string ackId, string userId = "abc-123") =>
        new(ackId, $$"""{ "user": "users/{{userId}}", "dataType": "heart-rate" }""");

    [Fact]
    public async Task ABurstForOneUser_SyncsTheConnectionOnce_AndAcksEverything()
    {
        SetupOneBatch(Notification("ack-1"), Notification("ack-2"));
        _connections.GetSyncableByHealthUserIdAsync("abc-123").Returns([_connection]);

        var summary = await CreateSut().DrainAsync();

        await _sync.Received(1).SyncCardiMemberAsync(_connection, SyncScope.WorkerCadence);
        await _source.Received(1).AcknowledgeAsync(
            Arg.Is<IReadOnlyList<string>>(ids => ids.Contains("ack-1") && ids.Contains("ack-2")),
            Arg.Any<CancellationToken>());
        Assert.Equal(2, summary.Messages);
        Assert.Equal(1, summary.SyncedConnections);
    }

    // A wearer who disconnected (or whose member paused monitoring — the lookup enforces both)
    // is a fact, not an error: acknowledge and move on, or the message loops forever.
    [Fact]
    public async Task AnUnknownUser_IsAcked_AndNeverSynced()
    {
        SetupOneBatch(Notification("ack-1", "gone-user"));
        _connections.GetSyncableByHealthUserIdAsync("gone-user").Returns([]);

        var summary = await CreateSut().DrainAsync();

        await _sync.DidNotReceive().SyncCardiMemberAsync(Arg.Any<DeviceConnection>(), Arg.Any<SyncScope>());
        await _source.Received(1).AcknowledgeAsync(
            Arg.Is<IReadOnlyList<string>>(ids => ids.Contains("ack-1")), Arg.Any<CancellationToken>());
        Assert.Equal(1, summary.UnknownUsers);
    }

    // Poison tolerance: an unreadable notification is acknowledged because the routine poll
    // guarantees the data is not lost — leaving it unacked would redeliver it forever.
    [Fact]
    public async Task AnUnparseableNotification_IsAcked_AndCounted()
    {
        SetupOneBatch(new ReceivedNotification("ack-1", "not json"));

        var summary = await CreateSut().DrainAsync();

        Assert.Equal(1, summary.Unparseable);
        await _source.Received(1).AcknowledgeAsync(
            Arg.Is<IReadOnlyList<string>>(ids => ids.Contains("ack-1")), Arg.Any<CancellationToken>());
    }

    // The inverse rule: a failed sync must NOT be acknowledged — redelivery retries it next run,
    // and the sync is idempotent end to end.
    [Fact]
    public async Task ASyncFailure_LeavesTheUsersMessagesUnacked()
    {
        SetupOneBatch(Notification("ack-1"), new ReceivedNotification("ack-2", "not json"));
        _connections.GetSyncableByHealthUserIdAsync("abc-123").Returns([_connection]);
        _sync.SyncCardiMemberAsync(Arg.Any<DeviceConnection>(), Arg.Any<SyncScope>())
            .ThrowsAsync(new InvalidOperationException("provider down"));

        var summary = await CreateSut().DrainAsync();

        Assert.Equal(1, summary.FailedUsers);
        await _source.Received(1).AcknowledgeAsync(
            Arg.Is<IReadOnlyList<string>>(ids => ids.Contains("ack-2") && !ids.Contains("ack-1")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnEmptySubscription_DrainsToNothing()
    {
        _source.PullAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);

        var summary = await CreateSut().DrainAsync();

        Assert.Equal(0, summary.Messages);
        await _source.Received(1).PullAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }
}
