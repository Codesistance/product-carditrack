using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.UnitTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace CardiTrack.UnitTests.Repositories;

/// <summary>
/// The Advise observation log's queries, against a real Postgres.
/// </summary>
/// <remarks>
/// Here rather than in a service test because the thing worth pinning is the SQL:
/// <c>GetLatestPerTopicAsync</c> groups and takes the first of each group, which a provider is
/// free to refuse to translate and fall back to evaluating in memory. A substitute would answer
/// happily and prove nothing.
/// </remarks>
[Collection("DatabaseCollection")]
public class MemberAdviseObservationRepositoryTests(TestDatabaseFixture fixture)
{
    private static MemberAdviseObservation Entry(
        Guid memberId, AdviseTopic topic, string summary, DateTime observedAtUtc) => new()
        {
            CardiMemberId = memberId,
            Topic = topic,
            Summary = summary,
            Suggestion = "A short walk after lunch is worth trying.",
            GuidelineCited = "WHO adult activity guidance",
            ObservedAtUtc = observedAtUtc,
        };

    private static async Task<Guid> SeedMemberAsync(IServiceScope scope)
    {
        var org = await TestDataSeeder.SeedOrganizationAsync(scope);
        var member = await TestDataSeeder.SeedCardiMemberAsync(scope, org.Id);
        return member.Id;
    }

    [Fact]
    public async Task GetLatestPerTopicAsync_ReturnsTheNewestEntryForEachTopic()
    {
        using var scope = fixture.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IMemberAdviseObservationRepository>();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var memberId = await SeedMemberAsync(scope);
        var now = DateTime.UtcNow;

        await repo.AddAsync(Entry(memberId, AdviseTopic.Activity, "Steps were down.", now.AddDays(-10)));
        await repo.AddAsync(Entry(memberId, AdviseTopic.Activity, "Steps were back up.", now.AddDays(-2)));
        await repo.AddAsync(Entry(memberId, AdviseTopic.Sleep, "Sleep was shorter.", now.AddDays(-5)));
        await uow.SaveChangesAsync();

        var latest = await repo.GetLatestPerTopicAsync(memberId);

        Assert.Equal(2, latest.Count);
        Assert.Equal("Steps were back up.", latest.Single(o => o.Topic == AdviseTopic.Activity).Summary);
        Assert.Equal("Sleep was shorter.", latest.Single(o => o.Topic == AdviseTopic.Sleep).Summary);
    }

    [Fact]
    public async Task GetLatestPerTopicAsync_IgnoresOtherMembers()
    {
        using var scope = fixture.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IMemberAdviseObservationRepository>();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var mine = await SeedMemberAsync(scope);
        var theirs = await SeedMemberAsync(scope);
        var now = DateTime.UtcNow;

        await repo.AddAsync(Entry(mine, AdviseTopic.Activity, "Mine.", now.AddDays(-3)));
        await repo.AddAsync(Entry(theirs, AdviseTopic.Activity, "Theirs, and newer.", now));
        await uow.SaveChangesAsync();

        var latest = await repo.GetLatestPerTopicAsync(mine);

        Assert.Equal("Mine.", Assert.Single(latest).Summary);
    }

    [Fact]
    public async Task GetLatestPerTopicAsync_IsEmptyForAMemberWithNothingLogged()
    {
        using var scope = fixture.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IMemberAdviseObservationRepository>();

        Assert.Empty(await repo.GetLatestPerTopicAsync(await SeedMemberAsync(scope)));
    }

    [Fact]
    public async Task GetByCardiMemberAsync_ReturnsTheWindowNewestFirst_WithAnExclusiveUpperBound()
    {
        using var scope = fixture.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IMemberAdviseObservationRepository>();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var memberId = await SeedMemberAsync(scope);
        var now = new DateTime(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc);

        await repo.AddAsync(Entry(memberId, AdviseTopic.Activity, "Too old.", now.AddDays(-30)));
        await repo.AddAsync(Entry(memberId, AdviseTopic.Activity, "In the window.", now.AddDays(-5)));
        await repo.AddAsync(Entry(memberId, AdviseTopic.Sleep, "Also in it, newer.", now.AddDays(-1)));
        // Exactly on the upper bound, which is exclusive so adjacent windows neither overlap nor gap.
        await repo.AddAsync(Entry(memberId, AdviseTopic.Sleep, "On the boundary.", now));
        await uow.SaveChangesAsync();

        var window = await repo.GetByCardiMemberAsync(memberId, now.AddDays(-10), now, limit: 50);

        Assert.Equal(["Also in it, newer.", "In the window."], window.Select(o => o.Summary));
    }

    [Fact]
    public async Task GetObservedBeforeAsync_AndDeleteObservedBeforeAsync_SweepOnlyTheExpired()
    {
        using var scope = fixture.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IMemberAdviseObservationRepository>();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var memberId = await SeedMemberAsync(scope);
        var now = DateTime.UtcNow;
        var cutoff = now.AddDays(-365);

        await repo.AddAsync(Entry(memberId, AdviseTopic.Activity, "Ancient.", cutoff.AddDays(-1)));
        await repo.AddAsync(Entry(memberId, AdviseTopic.Sleep, "Recent.", now.AddDays(-1)));
        await uow.SaveChangesAsync();

        var expired = await repo.GetObservedBeforeAsync(cutoff, take: 100);
        Assert.Equal("Ancient.", Assert.Single(expired).Summary);

        var deleted = await repo.DeleteObservedBeforeAsync([.. expired.Select(o => o.Id)], cutoff);
        Assert.Equal(1, deleted);

        var left = await repo.GetByCardiMemberAsync(memberId, now.AddYears(-5), now, limit: 50);
        Assert.Equal("Recent.", Assert.Single(left).Summary);
    }
}
