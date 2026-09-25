using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Persistence;
using CardiTrack.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace CardiTrack.IntegrationTests.Repositories;

/// <summary>
/// Whether a generation that outlived its lease can tell that someone else has written since.
/// Against a real Postgres and a real change tracker, because the answer turns entirely on EF
/// behaviour: the claim being tested is that a tracked read cannot answer the question and a
/// scalar projection can, and a substitute would agree with whichever one the test asserted.
/// </summary>
public class MemberInsightFreshnessTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithCleanUp(true)
        .Build();

    private ServiceProvider _services = null!;
    private readonly Guid _memberId = Guid.NewGuid();
    private static readonly DateTime FirstWrite = new(2026, 9, 7, 2, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime SuccessorWrite = new(2026, 9, 7, 2, 5, 0, DateTimeKind.Utc);

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        var sc = new ServiceCollection();
        sc.AddDbContext<CardiTrackDbContext>(options =>
            options.UseNpgsql(_container.GetConnectionString(),
                b => b.MigrationsAssembly("CardiTrack.Infrastructure")));
        _services = sc.BuildServiceProvider();

        using var scope = _services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>().Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        // The container goes even if StartAsync failed before the provider was built.
        try
        {
            if (_services is not null)
                await _services.DisposeAsync();
        }
        finally
        {
            await _container.DisposeAsync();
        }
    }

    [Fact]
    public async Task TheTrackedReadCannotSeeASuccessorsWrite_AndTheScalarReadCan()
    {
        await SeedAsync();

        // One scope, standing in for one execution of the pass.
        using var mine = _services.CreateScope();
        var repository = Repository(mine);

        // The probe this attempt makes before claiming. It puts the row in the change tracker.
        var probed = await repository.GetByScopeAsync(_memberId, InsightScope.TrendWeekly);
        Assert.NotNull(probed);
        Assert.Equal(FirstWrite, probed!.GeneratedAtUtc);

        // Meanwhile a successor takes over and writes, on its own connection.
        using (var successor = _services.CreateScope())
        {
            var db = successor.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
            var row = await db.Set<MemberInsight>()
                .SingleAsync(i => i.CardiMemberId == _memberId && i.Scope == InsightScope.TrendWeekly);
            row.Summary = "The successor's narrative.";
            row.GeneratedAtUtc = SuccessorWrite;
            await db.SaveChangesAsync();
        }

        // The tracked read still reports the values it loaded before the successor wrote. This is
        // correct EF behaviour and exactly why it cannot be the fence — a generation asking this
        // would conclude nothing had changed and save its own older narrative over the newer one.
        var readAgain = await repository.GetByScopeAsync(_memberId, InsightScope.TrendWeekly);
        Assert.Same(probed, readAgain);
        Assert.Equal(FirstWrite, readAgain!.GeneratedAtUtc);

        // The scalar projection goes to the database and reports what is actually stored.
        var storedAt = await repository.GetGeneratedAtUtcAsync(_memberId, InsightScope.TrendWeekly);
        Assert.Equal(SuccessorWrite, storedAt);
    }

    [Fact]
    public async Task TheScalarReadIsNullWhenNothingIsStored()
    {
        using var scope = _services.CreateScope();

        Assert.Null(await Repository(scope)
            .GetGeneratedAtUtcAsync(Guid.NewGuid(), InsightScope.TrendWeekly));
    }

    [Fact]
    public async Task EveryMemberHoldingTheScopeIsListed_AndOnlyThatScope()
    {
        await SeedAsync();

        var otherMember = Guid.NewGuid();
        using (var seed = _services.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
            db.Set<MemberInsight>().Add(Insight(otherMember, InsightScope.TrendWeekly));
            // A different scope for the same member must not put them in a weekly sweep.
            db.Set<MemberInsight>().Add(Insight(Guid.NewGuid(), InsightScope.TrendMonthly));
            await db.SaveChangesAsync();
        }

        using var scope = _services.CreateScope();
        var weekly = await Repository(scope).GetMemberIdsWithScopeAsync(InsightScope.TrendWeekly);

        Assert.Equal(2, weekly.Count);
        Assert.Contains(_memberId, weekly);
        Assert.Contains(otherMember, weekly);
    }

    private async Task SeedAsync()
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
        db.Set<MemberInsight>().Add(Insight(_memberId, InsightScope.TrendWeekly));
        await db.SaveChangesAsync();
    }

    private static MemberInsight Insight(Guid memberId, InsightScope scope) => new()
    {
        CardiMemberId = memberId,
        Scope = scope,
        Summary = "An account of the week just gone.",
        GeneratedAtUtc = FirstWrite,
        PromptVersion = 1,
    };

    private static MemberInsightRepository Repository(IServiceScope scope) =>
        new(scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>());
}
