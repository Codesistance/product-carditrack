using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Persistence;
using CardiTrack.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace CardiTrack.IntegrationTests.Repositories;

/// <summary>
/// The claim a once-per-period generator takes before it pays for a model call, against a real
/// Postgres. What is being proved is the thing a mocked repository cannot prove: that two
/// executions racing the same member and period cannot both come away holding it, because the
/// exclusivity is the database's and not the caller's.
/// </summary>
public class GenerationLeaseClaimTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithCleanUp(true)
        .Build();

    private ServiceProvider _services = null!;
    private readonly Guid _memberId = Guid.NewGuid();
    private static readonly DateOnly PeriodEnd = new(2026, 9, 6);
    private static readonly DateTime Now = new(2026, 9, 7, 2, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Term = TimeSpan.FromMinutes(20);

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
        await _services.DisposeAsync();
        await _container.DisposeAsync();
    }

    [Fact]
    public async Task OnlyOneOfTwoOverlappingExecutionsTakesTheClaim()
    {
        // The race the lease exists for: the digest job is scheduled every thirty minutes against
        // an hour's Cloud Run timeout, so a slow pass is still running when the next starts.
        using var firstScope = _services.CreateScope();
        using var secondScope = _services.CreateScope();

        var first = Repository(firstScope);
        var second = Repository(secondScope);

        Assert.True(await first.TryClaimAsync(_memberId, GenerationWork.Weekbook, PeriodEnd, Now, Term));
        Assert.False(await second.TryClaimAsync(_memberId, GenerationWork.Weekbook, PeriodEnd, Now, Term));
    }

    [Fact]
    public async Task TwoExecutionsRacingTheSameClaimAtOnceProduceExactlyOneHolder()
    {
        // The same property under genuine concurrency rather than in sequence. Ten contenders on
        // their own connections, started together: whatever order Postgres serialises them in,
        // the ON CONFLICT ... WHERE admits one.
        var scopes = Enumerable.Range(0, 10).Select(_ => _services.CreateScope()).ToList();
        try
        {
            var attempts = scopes.Select(scope => Task.Run(() =>
                Repository(scope).TryClaimAsync(_memberId, GenerationWork.Daybook, PeriodEnd, Now, Term)));

            var results = await Task.WhenAll(attempts);

            Assert.Equal(1, results.Count(claimed => claimed));
        }
        finally
        {
            foreach (var scope in scopes)
                scope.Dispose();
        }
    }

    [Fact]
    public async Task ReleasingLetsTheNextExecutionIn()
    {
        using var scope = _services.CreateScope();
        var leases = Repository(scope);

        Assert.True(await leases.TryClaimAsync(_memberId, GenerationWork.Monthbook, PeriodEnd, Now, Term));
        await leases.ReleaseAsync(_memberId, GenerationWork.Monthbook);

        // A failed generation is retried on the next pass rather than waiting out the lease.
        Assert.True(await leases.TryClaimAsync(_memberId, GenerationWork.Monthbook, PeriodEnd, Now, Term));
    }

    [Fact]
    public async Task ALapsedClaimIsTakenOverWithoutBeingReleased()
    {
        // The self-healing half. An execution killed by a deploy, an OOM or the job's own timeout
        // never reaches its release, and the period must not stay blocked for good.
        using var scope = _services.CreateScope();
        var leases = Repository(scope);

        Assert.True(await leases.TryClaimAsync(_memberId, GenerationWork.TrendWeekly, PeriodEnd, Now, Term));

        Assert.False(await leases.TryClaimAsync(
            _memberId, GenerationWork.TrendWeekly, PeriodEnd, Now.Add(Term).AddSeconds(-1), Term));
        Assert.True(await leases.TryClaimAsync(
            _memberId, GenerationWork.TrendWeekly, PeriodEnd, Now.Add(Term), Term));
    }

    [Fact]
    public async Task NextPeriodIsClaimableOnceTheLastOneHasLapsed()
    {
        // The row is keyed on (member, work) and carries the period, so next week's claim reuses
        // it. This is what keeps the table bounded at members x works with nothing to sweep.
        using var scope = _services.CreateScope();
        var leases = Repository(scope);

        Assert.True(await leases.TryClaimAsync(_memberId, GenerationWork.Weekbook, PeriodEnd, Now, Term));

        var nextWeek = Now.AddDays(7);
        Assert.True(await leases.TryClaimAsync(
            _memberId, GenerationWork.Weekbook, PeriodEnd.AddDays(7), nextWeek, Term));

        using var verify = _services.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
        var rows = await db.GenerationLeases
            .Where(l => l.CardiMemberId == _memberId && l.Work == GenerationWork.Weekbook)
            .ToListAsync();

        Assert.Single(rows);
        Assert.Equal(PeriodEnd.AddDays(7), rows[0].PeriodEnd);
    }

    [Fact]
    public async Task DifferentWorksAndMembersDoNotBlockEachOther()
    {
        using var scope = _services.CreateScope();
        var leases = Repository(scope);

        Assert.True(await leases.TryClaimAsync(_memberId, GenerationWork.Weekbook, PeriodEnd, Now, Term));

        // A member's Weekbook and their weekly trend are due on the same instant and must both run.
        Assert.True(await leases.TryClaimAsync(_memberId, GenerationWork.TrendWeekly, PeriodEnd, Now, Term));
        // And one member's claim says nothing about another's.
        Assert.True(await leases.TryClaimAsync(Guid.NewGuid(), GenerationWork.Weekbook, PeriodEnd, Now, Term));
    }

    private static GenerationLeaseRepository Repository(IServiceScope scope) =>
        new(scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>());
}
