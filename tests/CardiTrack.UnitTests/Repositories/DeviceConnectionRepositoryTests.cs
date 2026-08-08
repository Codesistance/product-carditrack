using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Enums;
using CardiTrack.UnitTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace CardiTrack.UnitTests.Repositories;

[Collection("DatabaseCollection")]
public class DeviceConnectionRepositoryTests(TestDatabaseFixture fixture)
{
    // ── GetActiveByCardiMemberIdAsync ────────────────────────────────────────────

    [Fact]
    public async Task GetActiveByCardiMemberIdAsync_ReturnsConnections_ForGivenMember()
    {
        using var scope = fixture.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IDeviceConnectionRepository>();

        var org = await TestDataSeeder.SeedOrganizationAsync(scope);
        var member = await TestDataSeeder.SeedCardiMemberAsync(scope, org.Id);
        var connection = await TestDataSeeder.SeedDeviceConnectionAsync(scope, member.Id);

        var result = await repo.GetActiveByCardiMemberIdAsync(member.Id);

        Assert.Contains(result, c => c.Id == connection.Id);
    }

    [Fact]
    public async Task GetActiveByCardiMemberIdAsync_ExcludesDisconnectedConnections()
    {
        using var scope = fixture.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IDeviceConnectionRepository>();

        var org = await TestDataSeeder.SeedOrganizationAsync(scope);
        var member = await TestDataSeeder.SeedCardiMemberAsync(scope, org.Id);
        var disconnected = await TestDataSeeder.SeedDeviceConnectionAsync(
            scope, member.Id, status: ConnectionStatus.Disconnected);

        var result = await repo.GetActiveByCardiMemberIdAsync(member.Id);

        Assert.DoesNotContain(result, c => c.Id == disconnected.Id);
    }

    [Fact]
    public async Task GetActiveByCardiMemberIdAsync_ExcludesInactiveConnections()
    {
        using var scope = fixture.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IDeviceConnectionRepository>();

        var org = await TestDataSeeder.SeedOrganizationAsync(scope);
        var member = await TestDataSeeder.SeedCardiMemberAsync(scope, org.Id);
        var inactive = await TestDataSeeder.SeedDeviceConnectionAsync(
            scope, member.Id, isActive: false);

        var result = await repo.GetActiveByCardiMemberIdAsync(member.Id);

        Assert.DoesNotContain(result, c => c.Id == inactive.Id);
    }

    // ── AnyActiveForCardiMembersAsync ────────────────────────────────────────────

    [Fact]
    public async Task AnyActiveForCardiMembersAsync_IsTrue_WhenAnyMemberHasAnActiveConnection()
    {
        using var scope = fixture.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IDeviceConnectionRepository>();

        var org = await TestDataSeeder.SeedOrganizationAsync(scope);
        var withoutDevice = await TestDataSeeder.SeedCardiMemberAsync(scope, org.Id);
        var withDevice = await TestDataSeeder.SeedCardiMemberAsync(scope, org.Id);
        await TestDataSeeder.SeedDeviceConnectionAsync(scope, withDevice.Id);

        Assert.True(await repo.AnyActiveForCardiMembersAsync([withoutDevice.Id, withDevice.Id]));
    }

    [Fact]
    public async Task AnyActiveForCardiMembersAsync_IsFalse_WhenMembersHaveNoConnections()
    {
        using var scope = fixture.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IDeviceConnectionRepository>();

        var org = await TestDataSeeder.SeedOrganizationAsync(scope);
        var member = await TestDataSeeder.SeedCardiMemberAsync(scope, org.Id);

        Assert.False(await repo.AnyActiveForCardiMembersAsync([member.Id]));
    }

    [Theory]
    [InlineData(ConnectionStatus.Disconnected, true)]
    [InlineData(ConnectionStatus.Connected, false)]
    [InlineData(ConnectionStatus.TokenExpired, false)]
    public async Task AnyActiveForCardiMembersAsync_IgnoresDisconnectedAndInactiveConnections(
        ConnectionStatus status, bool isActive)
    {
        using var scope = fixture.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IDeviceConnectionRepository>();

        var org = await TestDataSeeder.SeedOrganizationAsync(scope);
        var member = await TestDataSeeder.SeedCardiMemberAsync(scope, org.Id);
        await TestDataSeeder.SeedDeviceConnectionAsync(scope, member.Id, status: status, isActive: isActive);

        Assert.False(await repo.AnyActiveForCardiMembersAsync([member.Id]));
    }

    // This is what the app's onboarding status asks on every launch to decide whether to push the
    // add-device wizard. A device mid-fault is still a device the user owns, so every one of these
    // has to answer true — answering false sent people who already had a Fitbit back through
    // provider consent to "add" the one they were already wearing.
    [Theory]
    [InlineData(ConnectionStatus.Connected)]
    [InlineData(ConnectionStatus.SyncError)]
    [InlineData(ConnectionStatus.TokenExpired)]
    [InlineData(ConnectionStatus.AuthError)]
    public async Task AnyActiveForCardiMembersAsync_IsTrue_ForAnyConnectionThatIsNotDisconnected(
        ConnectionStatus status)
    {
        using var scope = fixture.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IDeviceConnectionRepository>();

        var org = await TestDataSeeder.SeedOrganizationAsync(scope);
        var member = await TestDataSeeder.SeedCardiMemberAsync(scope, org.Id);
        await TestDataSeeder.SeedDeviceConnectionAsync(scope, member.Id, status: status);

        Assert.True(await repo.AnyActiveForCardiMembersAsync([member.Id]));
    }

    // An empty member list must not reach the database — a user with no CardiMembers
    // hits this on every app launch.
    [Fact]
    public async Task AnyActiveForCardiMembersAsync_IsFalse_ForAnEmptyMemberList()
    {
        using var scope = fixture.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IDeviceConnectionRepository>();

        Assert.False(await repo.AnyActiveForCardiMembersAsync([]));
    }

    // ── GetDueForSyncAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task GetDueForSyncAsync_ReturnsConnection_WhenNeverSynced()
    {
        using var scope = fixture.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IDeviceConnectionRepository>();

        var org = await TestDataSeeder.SeedOrganizationAsync(scope);
        var member = await TestDataSeeder.SeedCardiMemberAsync(scope, org.Id);
        var connection = await TestDataSeeder.SeedDeviceConnectionAsync(
            scope, member.Id, lastSyncDate: null);

        var result = await repo.GetDueForSyncAsync();

        Assert.Contains(result, c => c.Id == connection.Id);
    }

    [Fact]
    public async Task GetDueForSyncAsync_ReturnsConnection_WhenLastSyncExceedsThreshold()
    {
        using var scope = fixture.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IDeviceConnectionRepository>();

        var org = await TestDataSeeder.SeedOrganizationAsync(scope);
        var member = await TestDataSeeder.SeedCardiMemberAsync(scope, org.Id);
        var connection = await TestDataSeeder.SeedDeviceConnectionAsync(
            scope, member.Id, lastSyncDate: DateTime.UtcNow.AddMinutes(-45));

        var result = await repo.GetDueForSyncAsync();

        Assert.Contains(result, c => c.Id == connection.Id);
    }

    [Fact]
    public async Task GetDueForSyncAsync_ExcludesConnection_WhenRecentlySynced()
    {
        using var scope = fixture.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IDeviceConnectionRepository>();

        var org = await TestDataSeeder.SeedOrganizationAsync(scope);
        var member = await TestDataSeeder.SeedCardiMemberAsync(scope, org.Id);
        var connection = await TestDataSeeder.SeedDeviceConnectionAsync(
            scope, member.Id, lastSyncDate: DateTime.UtcNow.AddMinutes(-5));

        var result = await repo.GetDueForSyncAsync();

        Assert.DoesNotContain(result, c => c.Id == connection.Id);
    }

    [Fact]
    public async Task GetDueForSyncAsync_ExcludesConnection_WhenMonitoringIsPaused()
    {
        using var scope = fixture.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IDeviceConnectionRepository>();

        var org = await TestDataSeeder.SeedOrganizationAsync(scope);
        var member = await TestDataSeeder.SeedCardiMemberAsync(
            scope, org.Id, monitoringPausedUntil: DateTime.UtcNow.AddHours(12));
        var connection = await TestDataSeeder.SeedDeviceConnectionAsync(
            scope, member.Id, lastSyncDate: null);

        var result = await repo.GetDueForSyncAsync();

        // Pausing monitoring has to stop the data actually being collected, not just change
        // what the app shows.
        Assert.DoesNotContain(result, c => c.Id == connection.Id);
    }

    [Fact]
    public async Task GetDueForSyncAsync_ReturnsConnection_WhenPauseHasElapsed()
    {
        using var scope = fixture.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IDeviceConnectionRepository>();

        var org = await TestDataSeeder.SeedOrganizationAsync(scope);
        var member = await TestDataSeeder.SeedCardiMemberAsync(
            scope, org.Id, monitoringPausedUntil: DateTime.UtcNow.AddHours(-1));
        var connection = await TestDataSeeder.SeedDeviceConnectionAsync(
            scope, member.Id, lastSyncDate: null);

        var result = await repo.GetDueForSyncAsync();

        // A pause expires on its own — nothing has to run to un-pause a member.
        Assert.Contains(result, c => c.Id == connection.Id);
    }

    [Fact]
    public async Task GetDueForSyncAsync_ExcludesConnection_WhenMemberIsRemoved()
    {
        using var scope = fixture.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IDeviceConnectionRepository>();

        var org = await TestDataSeeder.SeedOrganizationAsync(scope);
        var member = await TestDataSeeder.SeedCardiMemberAsync(scope, org.Id, isActive: false);
        var connection = await TestDataSeeder.SeedDeviceConnectionAsync(
            scope, member.Id, lastSyncDate: null);

        var result = await repo.GetDueForSyncAsync();

        Assert.DoesNotContain(result, c => c.Id == connection.Id);
    }

    // The interval is the connection's own SyncFrequencyMinutes, not a fixed 30 — a connection
    // configured to sync slowly must not come due just because 30 minutes have passed.
    [Fact]
    public async Task GetDueForSyncAsync_ExcludesConnection_WhenItsOwnLongerIntervalHasNotElapsed()
    {
        using var scope = fixture.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IDeviceConnectionRepository>();

        var org = await TestDataSeeder.SeedOrganizationAsync(scope);
        var member = await TestDataSeeder.SeedCardiMemberAsync(scope, org.Id);
        var connection = await TestDataSeeder.SeedDeviceConnectionAsync(
            scope, member.Id,
            lastSyncDate: DateTime.UtcNow.AddMinutes(-45),
            syncFrequencyMinutes: 240);

        var result = await repo.GetDueForSyncAsync();

        Assert.DoesNotContain(result, c => c.Id == connection.Id);
    }

    [Fact]
    public async Task GetDueForSyncAsync_ReturnsConnection_WhenItsOwnShorterIntervalHasElapsed()
    {
        using var scope = fixture.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IDeviceConnectionRepository>();

        var org = await TestDataSeeder.SeedOrganizationAsync(scope);
        var member = await TestDataSeeder.SeedCardiMemberAsync(scope, org.Id);
        var connection = await TestDataSeeder.SeedDeviceConnectionAsync(
            scope, member.Id,
            lastSyncDate: DateTime.UtcNow.AddMinutes(-10),
            syncFrequencyMinutes: 5);

        var result = await repo.GetDueForSyncAsync();

        Assert.Contains(result, c => c.Id == connection.Id);
    }

    // A provider that failed us last run is not a connection the user has to repair, and nothing
    // else puts it back in service — the status only returns to Connected on a sync that would
    // never be scheduled. Excluding it retired healthy devices on one bad response.
    [Fact]
    public async Task GetDueForSyncAsync_ReturnsConnection_WhenItsLastPullErrored()
    {
        using var scope = fixture.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IDeviceConnectionRepository>();

        var org = await TestDataSeeder.SeedOrganizationAsync(scope);
        var member = await TestDataSeeder.SeedCardiMemberAsync(scope, org.Id);
        var connection = await TestDataSeeder.SeedDeviceConnectionAsync(
            scope, member.Id, status: ConnectionStatus.SyncError, lastSyncDate: null);

        var result = await repo.GetDueForSyncAsync();

        Assert.Contains(result, c => c.Id == connection.Id);
    }

    // The other side of it: these mean the provider refused our credentials, and only re-consent
    // produces working ones. Retrying just spends requests to be refused again.
    [Theory]
    [InlineData(ConnectionStatus.TokenExpired)]
    [InlineData(ConnectionStatus.AuthError)]
    [InlineData(ConnectionStatus.Disconnected)]
    public async Task GetDueForSyncAsync_ExcludesConnection_WhenItNeedsReconsent(ConnectionStatus status)
    {
        using var scope = fixture.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IDeviceConnectionRepository>();

        var org = await TestDataSeeder.SeedOrganizationAsync(scope);
        var member = await TestDataSeeder.SeedCardiMemberAsync(scope, org.Id);
        var connection = await TestDataSeeder.SeedDeviceConnectionAsync(
            scope, member.Id, status: status, lastSyncDate: null);

        var result = await repo.GetDueForSyncAsync();

        Assert.DoesNotContain(result, c => c.Id == connection.Id);
    }

    // ── UpdateTokenAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateTokenAsync_PersistsNewTokensAndExpiry()
    {
        Guid connectionId;
        var newExpiry = DateTime.UtcNow.AddHours(24);

        // Arrange + Act
        using (var scope = fixture.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IDeviceConnectionRepository>();
            var org = await TestDataSeeder.SeedOrganizationAsync(scope);
            var member = await TestDataSeeder.SeedCardiMemberAsync(scope, org.Id);
            var connection = await TestDataSeeder.SeedDeviceConnectionAsync(scope, member.Id);
            connectionId = connection.Id;
            await repo.UpdateTokenAsync(connectionId, "new_access_enc", "new_refresh_enc", newExpiry);
        }

        // Assert — fresh scope bypasses EF change-tracking cache
        using var readScope = fixture.CreateScope();
        var readRepo = readScope.ServiceProvider.GetRequiredService<IDeviceConnectionRepository>();
        var updated = await readRepo.GetByIdAsync(connectionId);
        Assert.Equal("new_access_enc", updated!.AccessToken);
        Assert.Equal("new_refresh_enc", updated.RefreshToken);
        Assert.Equal(newExpiry.Date, updated.TokenExpiry!.Value.Date);
    }

    // ── UpdateStatusAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateStatusAsync_PersistsNewStatus()
    {
        Guid connectionId;

        using (var scope = fixture.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IDeviceConnectionRepository>();
            var org = await TestDataSeeder.SeedOrganizationAsync(scope);
            var member = await TestDataSeeder.SeedCardiMemberAsync(scope, org.Id);
            var connection = await TestDataSeeder.SeedDeviceConnectionAsync(scope, member.Id);
            connectionId = connection.Id;
            await repo.UpdateStatusAsync(connectionId, ConnectionStatus.TokenExpired);
        }

        using var readScope = fixture.CreateScope();
        var readRepo = readScope.ServiceProvider.GetRequiredService<IDeviceConnectionRepository>();
        var updated = await readRepo.GetByIdAsync(connectionId);
        Assert.Equal(ConnectionStatus.TokenExpired, updated!.ConnectionStatus);
    }

    // ── MarkSyncSucceededAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task MarkSyncSucceededAsync_PersistsNewSyncDate()
    {
        Guid connectionId;
        var syncDate = DateTime.UtcNow;

        using (var scope = fixture.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IDeviceConnectionRepository>();
            var org = await TestDataSeeder.SeedOrganizationAsync(scope);
            var member = await TestDataSeeder.SeedCardiMemberAsync(scope, org.Id);
            var connection = await TestDataSeeder.SeedDeviceConnectionAsync(scope, member.Id);
            connectionId = connection.Id;
            await repo.MarkSyncSucceededAsync(connectionId, syncDate);
        }

        using var readScope = fixture.CreateScope();
        var readRepo = readScope.ServiceProvider.GetRequiredService<IDeviceConnectionRepository>();
        var updated = await readRepo.GetByIdAsync(connectionId);
        Assert.NotNull(updated!.LastSyncDate);
        Assert.Equal(syncDate.Date, updated.LastSyncDate!.Value.Date);
    }

    // The window landed, so whatever the last run hit, the connection works — leaving the fault
    // on the row would keep the app reporting a device that needs attention when it does not.
    [Theory]
    [InlineData(ConnectionStatus.SyncError)]
    [InlineData(ConnectionStatus.TokenExpired)]
    [InlineData(ConnectionStatus.AuthError)]
    public async Task MarkSyncSucceededAsync_ClearsAFaultedStatus(ConnectionStatus status)
    {
        Guid connectionId;

        using (var scope = fixture.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IDeviceConnectionRepository>();
            var org = await TestDataSeeder.SeedOrganizationAsync(scope);
            var member = await TestDataSeeder.SeedCardiMemberAsync(scope, org.Id);
            var connection = await TestDataSeeder.SeedDeviceConnectionAsync(
                scope, member.Id, status: status);
            connectionId = connection.Id;
            await repo.MarkSyncSucceededAsync(connectionId, DateTime.UtcNow);
        }

        using var readScope = fixture.CreateScope();
        var readRepo = readScope.ServiceProvider.GetRequiredService<IDeviceConnectionRepository>();
        var updated = await readRepo.GetByIdAsync(connectionId);
        Assert.Equal(ConnectionStatus.Connected, updated!.ConnectionStatus);
    }

    // A pull already in flight when the user removes the device must not hand it back to them.
    [Theory]
    [InlineData(ConnectionStatus.Disconnected, true)]
    [InlineData(ConnectionStatus.Connected, false)]
    public async Task MarkSyncSucceededAsync_LeavesATornDownConnectionAlone(
        ConnectionStatus status, bool isActive)
    {
        Guid connectionId;

        using (var scope = fixture.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IDeviceConnectionRepository>();
            var org = await TestDataSeeder.SeedOrganizationAsync(scope);
            var member = await TestDataSeeder.SeedCardiMemberAsync(scope, org.Id);
            var connection = await TestDataSeeder.SeedDeviceConnectionAsync(
                scope, member.Id, status: status, isActive: isActive);
            connectionId = connection.Id;
            await repo.MarkSyncSucceededAsync(connectionId, DateTime.UtcNow);
        }

        using var readScope = fixture.CreateScope();
        var readRepo = readScope.ServiceProvider.GetRequiredService<IDeviceConnectionRepository>();
        var updated = await readRepo.GetByIdAsync(connectionId);
        Assert.Equal(status, updated!.ConnectionStatus);
        Assert.Null(updated.LastSyncDate);
    }

    // ── GetRandomSyncableSampleAsync ─────────────────────────────────────────────
    //
    // The audit pull's input. It fetches a member's health data exactly as a routine pull does, so
    // it inherits the same eligibility rules — measuring is not an exemption from them.

    [Fact]
    public async Task GetRandomSyncableSampleAsync_ReturnsEligibleConnections()
    {
        using var scope = fixture.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IDeviceConnectionRepository>();

        var org = await TestDataSeeder.SeedOrganizationAsync(scope);
        var member = await TestDataSeeder.SeedCardiMemberAsync(scope, org.Id);
        var connection = await TestDataSeeder.SeedDeviceConnectionAsync(scope, member.Id);

        var result = await repo.GetRandomSyncableSampleAsync(500);

        Assert.Contains(result, c => c.Id == connection.Id);
    }

    // Pausing monitoring has to actually stop collection, on every path that collects.
    [Fact]
    public async Task GetRandomSyncableSampleAsync_ExcludesPausedMembers()
    {
        using var scope = fixture.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IDeviceConnectionRepository>();

        var org = await TestDataSeeder.SeedOrganizationAsync(scope);
        var paused = await TestDataSeeder.SeedCardiMemberAsync(
            scope, org.Id, monitoringPausedUntil: DateTime.UtcNow.AddDays(7));
        var connection = await TestDataSeeder.SeedDeviceConnectionAsync(scope, paused.Id);

        var result = await repo.GetRandomSyncableSampleAsync(500);

        Assert.DoesNotContain(result, c => c.Id == connection.Id);
    }

    [Fact]
    public async Task GetRandomSyncableSampleAsync_ExcludesInactiveMembers()
    {
        using var scope = fixture.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IDeviceConnectionRepository>();

        var org = await TestDataSeeder.SeedOrganizationAsync(scope);
        var removed = await TestDataSeeder.SeedCardiMemberAsync(scope, org.Id, isActive: false);
        var connection = await TestDataSeeder.SeedDeviceConnectionAsync(scope, removed.Id);

        var result = await repo.GetRandomSyncableSampleAsync(500);

        Assert.DoesNotContain(result, c => c.Id == connection.Id);
    }

    [Fact]
    public async Task GetRandomSyncableSampleAsync_ExcludesDisconnectedConnections()
    {
        using var scope = fixture.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IDeviceConnectionRepository>();

        var org = await TestDataSeeder.SeedOrganizationAsync(scope);
        var member = await TestDataSeeder.SeedCardiMemberAsync(scope, org.Id);
        var disconnected = await TestDataSeeder.SeedDeviceConnectionAsync(
            scope, member.Id, status: ConnectionStatus.Disconnected);

        var result = await repo.GetRandomSyncableSampleAsync(500);

        Assert.DoesNotContain(result, c => c.Id == disconnected.Id);
    }

    // Unlike the routine query, due-ness is irrelevant here — a connection synced a minute ago is
    // still a valid subject for measuring how far back its provider revises data.
    [Fact]
    public async Task GetRandomSyncableSampleAsync_IncludesRecentlySyncedConnections()
    {
        using var scope = fixture.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IDeviceConnectionRepository>();

        var org = await TestDataSeeder.SeedOrganizationAsync(scope);
        var member = await TestDataSeeder.SeedCardiMemberAsync(scope, org.Id);
        var justSynced = await TestDataSeeder.SeedDeviceConnectionAsync(
            scope, member.Id, lastSyncDate: DateTime.UtcNow);

        var result = await repo.GetRandomSyncableSampleAsync(500);

        Assert.Contains(result, c => c.Id == justSynced.Id);
    }

    [Fact]
    public async Task GetRandomSyncableSampleAsync_CapsResultsAtRequestedCount()
    {
        using var scope = fixture.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IDeviceConnectionRepository>();

        var org = await TestDataSeeder.SeedOrganizationAsync(scope);
        var member = await TestDataSeeder.SeedCardiMemberAsync(scope, org.Id);
        await TestDataSeeder.SeedDeviceConnectionAsync(scope, member.Id);
        await TestDataSeeder.SeedDeviceConnectionAsync(scope, member.Id);
        await TestDataSeeder.SeedDeviceConnectionAsync(scope, member.Id);

        var result = await repo.GetRandomSyncableSampleAsync(2);

        Assert.Equal(2, result.Count());
    }

    // Guards the audit worker's own "sample size not positive" path from ever reaching the
    // database with an unbounded query.
    [Fact]
    public async Task GetRandomSyncableSampleAsync_ReturnsEmpty_WhenCountIsNotPositive()
    {
        using var scope = fixture.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IDeviceConnectionRepository>();

        var org = await TestDataSeeder.SeedOrganizationAsync(scope);
        var member = await TestDataSeeder.SeedCardiMemberAsync(scope, org.Id);
        await TestDataSeeder.SeedDeviceConnectionAsync(scope, member.Id);

        Assert.Empty(await repo.GetRandomSyncableSampleAsync(0));
        Assert.Empty(await repo.GetRandomSyncableSampleAsync(-1));
    }

    // The audit measures how faithfully healthy connections are being pulled, so unlike the
    // routine due query it stays on Connected alone — a connection mid-recovery from a provider
    // failure would move the number the audit exists to report without saying anything about
    // the pull. Guards the two filters against being "aligned" by a later change.
    [Fact]
    public async Task GetRandomSyncableSampleAsync_ExcludesSyncErroredConnections()
    {
        using var scope = fixture.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IDeviceConnectionRepository>();

        var org = await TestDataSeeder.SeedOrganizationAsync(scope);
        var member = await TestDataSeeder.SeedCardiMemberAsync(scope, org.Id);
        var errored = await TestDataSeeder.SeedDeviceConnectionAsync(
            scope, member.Id, status: ConnectionStatus.SyncError);

        var result = await repo.GetRandomSyncableSampleAsync(50);

        Assert.DoesNotContain(result, c => c.Id == errored.Id);
    }
}
