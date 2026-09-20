using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.UnitTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace CardiTrack.UnitTests.Repositories;

/// <summary>
/// What the explanation backfill is allowed to see, against the real database, because it is a SQL
/// predicate and the predicate is the contract.
/// </summary>
/// <remarks>
/// Two sets that are easy to confuse. What is still <em>happening</em> is the unresolved set. What
/// can still be <em>read</em> is larger: <c>AlertService.GetByIdAsync</c> gates on
/// <c>IsActive</c> alone, and <c>AlertResolution</c> sets <c>IsResolved</c> without touching it —
/// deliberately, so a closed episode stays in the archive. Anything acting on what a caregiver may
/// open has to use the second, and a substitute cannot prove which one the SQL asked for.
/// </remarks>
[Collection("DatabaseCollection")]
public class ServableAlertQueryTests(TestDatabaseFixture fixture)
{
    private static readonly DateTime Now = new(2026, 9, 20, 13, 40, 0, DateTimeKind.Utc);
    private static readonly DateTime Cutoff = Now.AddDays(-14);

    [Fact]
    public async Task AResolvedAlertInsideTheWindowIsStillServable()
    {
        using var scope = fixture.CreateScope();
        var (alerts, memberId) = await SeedAsync(
            scope,
            Alert("still open", Now.AddDays(-1), resolved: false),
            Alert("closed yesterday", Now.AddDays(-1), resolved: true));

        var servable = await alerts.GetServableByCardiMemberAsync(memberId, Cutoff);

        // The one that used to fall out of the backfill the moment its producer closed it, on a
        // card the detail screen goes on serving.
        Assert.Equal(2, servable.Count);
        Assert.Contains(servable, a => a.Title == "closed yesterday");
    }

    [Fact]
    public async Task AnUnresolvedAlertIsServableHoweverOldItIs()
    {
        // Strictly wider than the set this replaced: a long-standing episode is still a candidate
        // past the window, because it is still happening.
        using var scope = fixture.CreateScope();
        var (alerts, memberId) = await SeedAsync(
            scope, Alert("open for months", Now.AddDays(-120), resolved: false));

        var servable = await alerts.GetServableByCardiMemberAsync(memberId, Cutoff);

        Assert.Single(servable);
        Assert.Equal("open for months", servable[0].Title);
    }

    [Fact]
    public async Task AResolvedAlertPastTheWindowIsNotWalkedAgain()
    {
        // The bound. Every candidate costs an indexed lookup on each of the 288 passes a day, so
        // without a cutoff this walk would grow with the member's whole history forever.
        using var scope = fixture.CreateScope();
        var (alerts, memberId) = await SeedAsync(
            scope, Alert("closed last month", Now.AddDays(-40), resolved: true));

        Assert.Empty(await alerts.GetServableByCardiMemberAsync(memberId, Cutoff));
    }

    [Fact]
    public async Task ADeletedAlertIsNotServableWhicheverSideOfTheWindowItFallsOn()
    {
        // IsActive false is a caregiver having swiped the card away, and the detail endpoint
        // refuses it. Explaining one would be work no one can ever read.
        using var scope = fixture.CreateScope();
        var (alerts, memberId) = await SeedAsync(
            scope,
            Alert("swiped away", Now.AddDays(-1), resolved: false, active: false),
            Alert("swiped away after closing", Now.AddDays(-1), resolved: true, active: false));

        Assert.Empty(await alerts.GetServableByCardiMemberAsync(memberId, Cutoff));
    }

    /// <summary>
    /// The member-level counterpart, and the predicate that decides who the sweep can see at all.
    /// It has to agree with the per-member one exactly: a member the walk would find alerts for
    /// but this query omits is a member the sweep never reaches.
    /// </summary>
    [Fact]
    public async Task AMemberIsListedOnTheSameTermsTheirAlertsAre()
    {
        using var scope = fixture.CreateScope();
        var (alerts, memberId) = await SeedAsync(
            scope, Alert("closed yesterday", Now.AddDays(-1), resolved: true));

        var ids = await alerts.GetCardiMemberIdsWithServableAlertsAsync(Cutoff);

        Assert.Contains(memberId, ids);
    }

    [Fact]
    public async Task AMemberWhoseOnlyAlertsFallOutsideTheSetIsNotListed()
    {
        using var scope = fixture.CreateScope();
        var (alerts, memberId) = await SeedAsync(
            scope,
            Alert("closed last month", Now.AddDays(-40), resolved: true),
            Alert("swiped away", Now.AddDays(-1), resolved: false, active: false));

        Assert.DoesNotContain(memberId, await alerts.GetCardiMemberIdsWithServableAlertsAsync(Cutoff));
    }

    [Fact]
    public async Task AMemberIsListedOnce_HoweverManyAlertsTheyHold()
    {
        using var scope = fixture.CreateScope();
        var (alerts, memberId) = await SeedAsync(
            scope,
            Alert("one", Now.AddDays(-1), resolved: false),
            Alert("two", Now.AddDays(-2), resolved: false),
            Alert("three", Now.AddDays(-3), resolved: true));

        var ids = await alerts.GetCardiMemberIdsWithServableAlertsAsync(Cutoff);

        Assert.Single(ids, id => id == memberId);
    }

    private static Alert Alert(string title, DateTime triggeredDate, bool resolved, bool active = true) =>
        new()
        {
            AlertType = AlertType.Inactivity,
            Severity = AlertSeverity.Yellow,
            Title = title,
            Message = "They moved less than they normally do.",
            TriggeredDate = triggeredDate,
            MetricValues = """{"rule":"activity_decline","steps":1200,"baselineAvgSteps":5000}""",
            IsResolved = resolved,
            IsActive = active,
        };

    private static async Task<(IAlertRepository Alerts, Guid MemberId)> SeedAsync(
        IServiceScope scope, params Alert[] alerts)
    {
        var repo = scope.ServiceProvider.GetRequiredService<IAlertRepository>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var organization = await TestDataSeeder.SeedOrganizationAsync(scope);
        var member = await TestDataSeeder.SeedCardiMemberAsync(scope, organization.Id);

        foreach (var alert in alerts)
        {
            alert.CardiMemberId = member.Id;
            await repo.AddAsync(alert);
        }

        await unitOfWork.SaveChangesAsync();
        return (repo, member.Id);
    }
}
