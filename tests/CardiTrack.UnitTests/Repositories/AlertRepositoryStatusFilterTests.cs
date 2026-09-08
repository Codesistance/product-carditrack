using CardiTrack.Application.DTOs.Common;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.UnitTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace CardiTrack.UnitTests.Repositories;

/// <summary>
/// How <see cref="AlertQuery.Status"/> divides a member's alerts, against the real database
/// because the division is a SQL predicate over two columns and it is the predicate that is the
/// contract here.
/// </summary>
/// <remarks>
/// The Alerts screen shows "current alerts" and, behind "View Archived Alerts", the resolved
/// ones. Those two halves are <see cref="AlertStatusFilter.Open"/> and
/// <see cref="AlertStatusFilter.Resolved"/>, and the screen is only honest if they partition the
/// list: every alert in exactly one half. Before <c>Open</c> existed the current list asked for
/// no status at all, so resolved alerts sat in both halves and shared the one page of
/// <see cref="AlertQuery.DefaultLimit"/> rows with the open ones — which is how an alert could
/// still be colouring the dashboard hero while being off the end of the list a caregiver was
/// sent to. The last test here is that page, and it is the one that would have caught it.
/// </remarks>
[Collection("DatabaseCollection")]
public class AlertRepositoryStatusFilterTests(TestDatabaseFixture fixture)
{
    /// <summary>An alert with no member yet — <see cref="SeedAsync"/> attaches it to the one it seeds.</summary>
    private static Alert Build(
        bool resolved = false,
        bool acknowledged = false,
        bool isActive = true,
        DateTime? triggeredAt = null) => new()
        {
            AlertType = AlertType.HeartRate,
            Severity = AlertSeverity.Yellow,
            Title = "Elevated heart rate during rest",
            Message = "Resting heart rate is above the usual range.",
            TriggeredDate = triggeredAt ?? DateTime.UtcNow,
            AcknowledgedDate = acknowledged ? DateTime.UtcNow : null,
            IsResolved = resolved,
            IsActive = isActive,
        };

    private static async Task<(IAlertRepository Repo, Guid MemberId)> SeedAsync(
        IServiceScope scope, params Alert[] alerts)
    {
        var repo = scope.ServiceProvider.GetRequiredService<IAlertRepository>();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var org = await TestDataSeeder.SeedOrganizationAsync(scope);
        var member = await TestDataSeeder.SeedCardiMemberAsync(scope, org.Id);

        foreach (var alert in alerts)
        {
            alert.CardiMemberId = member.Id;
            await repo.AddAsync(alert);
        }

        await uow.SaveChangesAsync();
        return (repo, member.Id);
    }

    private static AlertQuery Query(Guid memberId, AlertStatusFilter? status, int? limit = null) =>
        new([memberId], Status: status, Limit: limit ?? AlertQuery.DefaultLimit);

    /// <summary>
    /// The set the mobile list asks for outside its archive, and the same one
    /// <c>GetUnresolvedByCardiMemberAsync</c> colours the dashboard hero from: acknowledging
    /// records that someone looked, not that the episode ended, so an acknowledged alert is
    /// still open.
    /// </summary>
    [Fact]
    public async Task Open_TakesNewAndAcknowledged_AndLeavesResolvedBehind()
    {
        using var scope = fixture.CreateScope();
        var (repo, memberId) = await SeedAsync(
            scope,
            Build(),
            Build(acknowledged: true),
            Build(resolved: true));

        var open = await repo.QueryAsync(Query(memberId, AlertStatusFilter.Open));

        Assert.Equal(2, open.Count);
        Assert.All(open, a => Assert.False(a.IsResolved));
        Assert.Equal(2, await repo.CountAsync(Query(memberId, AlertStatusFilter.Open)));
    }

    /// <summary>
    /// Open and Resolved partition the list — the promise the "View Archived Alerts" /
    /// "Back to current alerts" toggle makes. No alert may be in both halves, and none in
    /// neither.
    /// </summary>
    [Fact]
    public async Task OpenAndResolved_PartitionTheList()
    {
        using var scope = fixture.CreateScope();
        var (repo, memberId) = await SeedAsync(
            scope,
            Build(),
            Build(acknowledged: true),
            Build(resolved: true),
            Build(resolved: true, acknowledged: true));

        var everything = await repo.QueryAsync(Query(memberId, status: null));
        var open = await repo.QueryAsync(Query(memberId, AlertStatusFilter.Open));
        var archived = await repo.QueryAsync(Query(memberId, AlertStatusFilter.Resolved));

        Assert.Equal(4, everything.Count);
        Assert.Equal(everything.Count, open.Count + archived.Count);
        Assert.Empty(open.Select(a => a.Id).Intersect(archived.Select(a => a.Id)));
    }

    /// <summary>
    /// A soft-deleted row is the caregiver's own housekeeping and is gone from every half —
    /// <c>Open</c> must not be the filter that brings it back.
    /// </summary>
    [Fact]
    public async Task Open_StillLeavesOutADeletedAlert()
    {
        using var scope = fixture.CreateScope();
        var (repo, memberId) = await SeedAsync(scope, Build(isActive: false));

        Assert.Empty(await repo.QueryAsync(Query(memberId, AlertStatusFilter.Open)));
    }

    /// <summary>
    /// The bug this filter exists for. An open alert older than a page of resolved ones is off
    /// the end of the unfiltered list — while <c>ComputeHealthStatus</c> reads it regardless of
    /// age and keeps the hero yellow, so the summary card sends a caregiver to Alerts for an
    /// alert that is not on the page. Asking for the open set puts it back on it.
    /// </summary>
    [Fact]
    public async Task Open_ReachesAnAlertThatAPageOfResolvedOnesWouldPushOff()
    {
        const int pageSize = 5;
        var now = DateTime.UtcNow;

        var oldestOpen = Build(acknowledged: true, triggeredAt: now.AddDays(-30));
        var newerResolved = Enumerable.Range(1, pageSize)
            .Select(day => Build(resolved: true, triggeredAt: now.AddDays(-day)))
            .ToArray();

        using var scope = fixture.CreateScope();
        var (repo, memberId) = await SeedAsync(scope, [oldestOpen, .. newerResolved]);

        var unfiltered = await repo.QueryAsync(Query(memberId, status: null, limit: pageSize));
        Assert.DoesNotContain(unfiltered, a => a.Id == oldestOpen.Id);

        var open = await repo.QueryAsync(Query(memberId, AlertStatusFilter.Open, limit: pageSize));
        Assert.Contains(open, a => a.Id == oldestOpen.Id);
    }
}
