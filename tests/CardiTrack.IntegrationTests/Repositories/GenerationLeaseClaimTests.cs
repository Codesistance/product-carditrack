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

        Assert.NotNull(await Claim(firstScope, GenerationWork.Weekbook, Now));
        Assert.Null(await Claim(secondScope, GenerationWork.Weekbook, Now));
    }

    [Fact]
    public async Task TenExecutionsRacingTheSameClaimAtOnceProduceExactlyOneHolder()
    {
        // The same property under genuine concurrency rather than in sequence. Ten contenders on
        // their own connections, started together: whatever order Postgres serialises them in,
        // the ON CONFLICT ... WHERE admits one.
        var scopes = Enumerable.Range(0, 10).Select(_ => _services.CreateScope()).ToList();
        try
        {
            var attempts = scopes.Select(scope =>
                Task.Run(() => Claim(scope, GenerationWork.Daybook, Now)));

            var results = await Task.WhenAll(attempts);

            var holders = results.Where(id => id is not null).ToList();
            Assert.Single(holders);
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

        var claim = await Claim(scope, GenerationWork.Monthbook, Now);
        Assert.NotNull(claim);
        await Repository(scope).ReleaseAsync(claim!.Value);

        // A failed generation is retried on the next pass rather than waiting out the lease.
        Assert.NotNull(await Claim(scope, GenerationWork.Monthbook, Now));
    }

    [Fact]
    public async Task ALapsedClaimIsTakenOverWithoutBeingReleased()
    {
        // The self-healing half. An execution killed by a deploy, an OOM or the job's own timeout
        // never reaches its release, and the period must not stay blocked for good.
        using var scope = _services.CreateScope();

        Assert.NotNull(await Claim(scope, GenerationWork.TrendWeekly, Now));

        Assert.Null(await Claim(scope, GenerationWork.TrendWeekly, Now.Add(Term).AddSeconds(-1)));
        Assert.NotNull(await Claim(scope, GenerationWork.TrendWeekly, Now.Add(Term)));
    }

    [Fact]
    public async Task AnOverrunningHolderCannotReleaseItsSuccessorsClaim()
    {
        // The fencing case. A generation that runs past its lease is taken over by a later
        // execution, and then finishes and hits its own finally. Releasing by (member, work)
        // would delete the successor's live row and let a third execution in while the second was
        // still working — so the release is keyed on the claim, and a displaced holder's release
        // matches nothing.
        using var scope = _services.CreateScope();
        var leases = Repository(scope);

        var overrunning = await Claim(scope, GenerationWork.Weekbook, Now);
        Assert.NotNull(overrunning);

        // Its lease lapses and a successor takes over, minting a new claim.
        var successor = await Claim(scope, GenerationWork.Weekbook, Now.Add(Term));
        Assert.NotNull(successor);
        Assert.NotEqual(overrunning!.Value, successor!.Value);

        // The first execution finally finishes and releases what it thinks it holds.
        await leases.ReleaseAsync(overrunning.Value);

        // The successor still holds the period: a third execution is kept out.
        Assert.Null(await Claim(scope, GenerationWork.Weekbook, Now.Add(Term).AddMinutes(1)));

        using var verify = _services.CreateScope();
        var row = await verify.ServiceProvider.GetRequiredService<CardiTrackDbContext>()
            .GenerationLeases
            .SingleAsync(l => l.CardiMemberId == _memberId && l.Work == GenerationWork.Weekbook);
        Assert.Equal(successor.Value, row.Id);
    }

    [Fact]
    public async Task NextPeriodIsClaimableOnceTheLastOneHasLapsed()
    {
        // The row is keyed on (member, work) and carries the period, so next week's claim reuses
        // it. This is what keeps the table bounded at members x works with nothing to sweep.
        using var scope = _services.CreateScope();

        Assert.NotNull(await Claim(scope, GenerationWork.Weekbook, Now));
        Assert.NotNull(await Claim(scope, GenerationWork.Weekbook, Now.AddDays(7), PeriodEnd.AddDays(7)));

        using var verify = _services.CreateScope();
        var rows = await verify.ServiceProvider.GetRequiredService<CardiTrackDbContext>()
            .GenerationLeases
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

        Assert.NotNull(await Claim(scope, GenerationWork.Weekbook, Now));

        // A member's Weekbook and their weekly trend are due on the same instant and must both run.
        Assert.NotNull(await Claim(scope, GenerationWork.TrendWeekly, Now));
        // And one member's claim says nothing about another's.
        Assert.NotNull(await leases.TryClaimAsync(
            Guid.NewGuid(), GenerationWork.Weekbook, PeriodEnd, Now, Term));
    }

    private Task<Guid?> Claim(
        IServiceScope scope, GenerationWork work, DateTime utcNow, DateOnly? periodEnd = null) =>
        Repository(scope).TryClaimAsync(
            _memberId, work, periodEnd ?? PeriodEnd, utcNow, Term);

    private static GenerationLeaseRepository Repository(IServiceScope scope) =>
        new(scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>());
}
