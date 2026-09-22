using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.UnitTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace CardiTrack.UnitTests.Repositories;

/// <summary>
/// Against the real PostgreSQL container, for the same reason as
/// <c>EnvironmentalReadingRepositoryTests</c> — and for one more, learned the hard way. The first
/// version of this repository decided insert-versus-update with <c>RETURNING (xmax = 0)</c>, which
/// PostgreSQL refuses on a partitioned table. It did not degrade, it threw; and because the caller
/// treats episode writes as best-effort, every beat would have been discarded silently while the
/// day's counts looked healthy. Nothing short of the real database can catch that: a substitute
/// would have happily returned whatever it was told to.
/// </summary>
[Collection("DatabaseCollection")]
public class RhythmEpisodeRepositoryTests(TestDatabaseFixture fixture)
{
    private static DateTime RecentWindow => DateTime.UtcNow.Date.AddHours(-3);

    private static async Task EnsurePartitionsAsync(IServiceScope scope) =>
        await scope.ServiceProvider.GetRequiredService<ITimeSeriesPartitionService>()
            .EnsureUpcomingPartitionsAsync(daysAhead: 7);

    private static RhythmEpisode Episode(
        Guid memberId, Guid connectionId, DateTime windowStartUtc, int beatCount = 3, int meanRr = 793) => new()
        {
            CardiMemberId = memberId,
            DeviceConnectionId = connectionId,
            WindowStartUtc = windowStartUtc,
            WindowEndUtc = windowStartUtc.AddMinutes(5),
            NotificationStartUtc = windowStartUtc,
            Positive = true,
            BeatCount = beatCount,
            RrMilliseconds = [800, 760, 820],
            OffsetMillisFromStart = [0, 800, 1560],
            MeanRrMs = meanRr,
            MinRrMs = 760,
            MaxRrMs = 820,
            RmssdMs = 51,
            IngestedAtUtc = DateTime.UtcNow,
        };

    [Fact]
    public async Task UpsertAsync_InsertsThenUpdates_AndSaysWhichItDid()
    {
        using var scope = fixture.CreateScope();
        await EnsurePartitionsAsync(scope);
        var repo = scope.ServiceProvider.GetRequiredService<IRhythmEpisodeRepository>();

        var memberId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        var window = RecentWindow;

        Assert.True(await repo.UpsertAsync(Episode(memberId, connectionId, window)));

        // The routine window re-reads the last three days on every pull, so the same window
        // arrives repeatedly and must land once — with the later reading winning.
        Assert.False(await repo.UpsertAsync(
            Episode(memberId, connectionId, window, beatCount: 5, meanRr: 812)));

        var stored = Assert.Single(
            await repo.GetInRangeAsync(memberId, window.AddMinutes(-1), window.AddMinutes(1)));
        Assert.Equal(5, stored.BeatCount);
        Assert.Equal(812, stored.MeanRrMs);
    }

    [Fact]
    public async Task UpsertAsync_KeepsBothDevices_ForTheSameWindow()
    {
        using var scope = fixture.CreateScope();
        await EnsurePartitionsAsync(scope);
        var repo = scope.ServiceProvider.GetRequiredService<IRhythmEpisodeRepository>();

        var memberId = Guid.NewGuid();
        var window = RecentWindow;

        // Two watches on one wearer can cover the same minutes, and each window is a measurement
        // one of them made. Keyed on (member, window) alone, the second would erase the first.
        Assert.True(await repo.UpsertAsync(Episode(memberId, Guid.NewGuid(), window)));
        Assert.True(await repo.UpsertAsync(Episode(memberId, Guid.NewGuid(), window)));

        var stored = await repo.GetInRangeAsync(memberId, window.AddMinutes(-1), window.AddMinutes(1));
        Assert.Equal(2, stored.Count);
    }

    [Fact]
    public async Task GetInRangeAsync_IsBoundedAndOrdered()
    {
        using var scope = fixture.CreateScope();
        await EnsurePartitionsAsync(scope);
        var repo = scope.ServiceProvider.GetRequiredService<IRhythmEpisodeRepository>();

        var memberId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        var window = RecentWindow;

        await repo.UpsertAsync(Episode(memberId, connectionId, window));
        await repo.UpsertAsync(Episode(memberId, connectionId, window.AddMinutes(10)));
        await repo.UpsertAsync(Episode(memberId, connectionId, window.AddHours(2)));

        var stored = await repo.GetInRangeAsync(memberId, window, window.AddMinutes(30));

        Assert.Equal(2, stored.Count);
        Assert.True(stored[0].WindowStartUtc < stored[1].WindowStartUtc);
    }

    [Fact]
    public async Task GetInRangeAsync_LeavesOtherMembersAlone()
    {
        using var scope = fixture.CreateScope();
        await EnsurePartitionsAsync(scope);
        var repo = scope.ServiceProvider.GetRequiredService<IRhythmEpisodeRepository>();

        var window = RecentWindow;
        var mine = Guid.NewGuid();
        await repo.UpsertAsync(Episode(mine, Guid.NewGuid(), window));
        await repo.UpsertAsync(Episode(Guid.NewGuid(), Guid.NewGuid(), window));

        var stored = await repo.GetInRangeAsync(mine, window.AddMinutes(-1), window.AddMinutes(1));

        Assert.Equal(mine, Assert.Single(stored).CardiMemberId);
    }

    [Fact]
    public async Task UpsertAsync_RoundTripsTheBeatArrays()
    {
        using var scope = fixture.CreateScope();
        await EnsurePartitionsAsync(scope);
        var repo = scope.ServiceProvider.GetRequiredService<IRhythmEpisodeRepository>();

        var memberId = Guid.NewGuid();
        var window = RecentWindow;
        await repo.UpsertAsync(Episode(memberId, Guid.NewGuid(), window));

        var stored = Assert.Single(
            await repo.GetInRangeAsync(memberId, window.AddMinutes(-1), window.AddMinutes(1)));

        // integer[] columns, so the ordering and the pairing between the two arrays are the thing
        // that has to survive the round trip — they are read back together as one series.
        Assert.Equal([800, 760, 820], stored.RrMilliseconds);
        Assert.Equal([0, 800, 1560], stored.OffsetMillisFromStart);
        Assert.Equal(51, stored.RmssdMs);
    }

    [Fact]
    public async Task UpsertAsync_SucceedsFourteenDaysBack_WhichIsWhatTheAuditPassCanReach()
    {
        // EnsureUpcomingPartitionsAsync's backward reach used to stop at yesterday regardless of
        // the caller's daysAhead. AuditSyncAsync's repair window is
        // Math.Max(SyncLookbackDays, AuditLookbackDays) -- 14 days by default -- and it writes
        // rhythm episodes through the same PullWindowAsync every routine sync uses. Pre-fix, an
        // insert into a day that far back would throw "no partition found for row", which
        // StoreRhythmEpisodesAsync's best-effort catch swallows: every beat from the audit pass
        // silently lost while the day's counts still looked healthy. No other write path ever
        // reaches this far back for RhythmEpisodes specifically (EnsurePartitionsForRangeAsync,
        // used by the history re-pull, deliberately excludes this table), so a partition this far
        // back can only exist if EnsureUpcomingPartitionsAsync's own backward range created it.
        using var scope = fixture.CreateScope();
        await EnsurePartitionsAsync(scope);
        var repo = scope.ServiceProvider.GetRequiredService<IRhythmEpisodeRepository>();

        var memberId = Guid.NewGuid();
        var window = DateTime.UtcNow.Date.AddDays(-14).AddHours(3);

        var thrown = await Record.ExceptionAsync(
            () => repo.UpsertAsync(Episode(memberId, Guid.NewGuid(), window)));

        Assert.Null(thrown);
        Assert.Single(await repo.GetInRangeAsync(memberId, window.AddMinutes(-1), window.AddMinutes(1)));
    }
}
