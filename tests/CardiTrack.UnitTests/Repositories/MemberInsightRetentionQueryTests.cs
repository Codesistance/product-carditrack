using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.UnitTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace CardiTrack.UnitTests.Repositories;

/// <summary>
/// The two halves of the retention sweep, against the real database, because both are SQL
/// predicates and it is the predicate that is the contract.
/// </summary>
/// <remarks>
/// One decides what may be deleted — and must never select an alert explanation, which the read
/// path serves however old it is. The other decides what actually is deleted, restating the age
/// so a row the digest or trend pass refreshed between the two statements survives. Neither can
/// be checked with a substitute: a mock returns whatever it was told to.
/// </remarks>
[Collection("DatabaseCollection")]
public class MemberInsightRetentionQueryTests(TestDatabaseFixture fixture)
{
    private static readonly DateTime Cutoff = new(2026, 6, 22, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task TheSweepSelectsOldMemberScopedRows_AndNeverAnAlertExplanation()
    {
        using var scope = fixture.CreateScope();
        var (repo, memberId) = await SeedAsync(
            scope,
            Insight(InsightScope.Baseline, Cutoff.AddDays(-1)),
            Insight(InsightScope.Trend, Cutoff.AddDays(-30)),
            Insight(InsightScope.Alert, Cutoff.AddDays(-200), alertId: Guid.NewGuid()));

        var expired = await repo.GetGeneratedBeforeAsync(Cutoff, take: 50);

        // Filtered to this test's member: the fixture's database is shared across the collection.
        var mine = expired.Where(i => i.CardiMemberId == memberId).ToList();
        Assert.Equal(2, mine.Count);
        Assert.All(mine, i => Assert.Null(i.AlertId));
        Assert.Contains(mine, i => i.Scope == InsightScope.Baseline);
        Assert.Contains(mine, i => i.Scope == InsightScope.Trend);
    }

    [Fact]
    public async Task TheDeleteSparesARowRefreshedSinceItWasSelected()
    {
        using var scope = fixture.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var (repo, memberId) = await SeedAsync(
            scope,
            Insight(InsightScope.Baseline, Cutoff.AddDays(-1)),
            Insight(InsightScope.Trend, Cutoff.AddDays(-1)));

        // This test's member only: the fixture's database is shared across the collection, so the
        // sweep legitimately sees other tests' rows too.
        var expired = (await repo.GetGeneratedBeforeAsync(Cutoff, take: 50))
            .Where(i => i.CardiMemberId == memberId)
            .ToList();
        Assert.Equal(2, expired.Count);

        // The race: one of the two is rewritten by a pass between the select and the delete.
        var refreshed = expired.First(i => i.Scope == InsightScope.Trend);
        refreshed.GeneratedAtUtc = Cutoff.AddDays(5);
        refreshed.Summary = "Written seconds ago.";
        repo.Update(refreshed);
        await unitOfWork.SaveChangesAsync();

        var deleted = await repo.DeleteGeneratedBeforeAsync(
            expired.Select(i => i.Id).ToList(), Cutoff);

        // Only the one that is still old. Deleting by key alone would have taken both.
        Assert.Equal(1, deleted);
        Assert.NotNull(await repo.GetByIdAsync(refreshed.Id));
    }

    /// <summary>Member left unset — <see cref="SeedAsync"/> attaches it to the one it creates.</summary>
    private static MemberInsight Insight(
        InsightScope scope, DateTime generatedAtUtc, Guid? alertId = null) => new()
        {
            Scope = scope,
            AlertId = alertId,
            Summary = "Something worth saying.",
            GeneratedAtUtc = generatedAtUtc,
        };

    private static async Task<(IMemberInsightRepository Repo, Guid MemberId)> SeedAsync(
        IServiceScope scope, params MemberInsight[] insights)
    {
        var repo = scope.ServiceProvider.GetRequiredService<IMemberInsightRepository>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var organization = await TestDataSeeder.SeedOrganizationAsync(scope);
        var member = await TestDataSeeder.SeedCardiMemberAsync(scope, organization.Id);

        foreach (var insight in insights)
        {
            insight.CardiMemberId = member.Id;
            await repo.AddAsync(insight);
        }

        await unitOfWork.SaveChangesAsync();
        return (repo, member.Id);
    }
}
