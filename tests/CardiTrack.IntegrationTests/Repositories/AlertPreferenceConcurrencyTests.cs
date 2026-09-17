using CardiTrack.Domain.Entities;
using CardiTrack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace CardiTrack.IntegrationTests.Repositories;

/// <summary>
/// The disabled-rule list is one JSON value read, modified and written whole, and it has two
/// writers now — the settings page and a chat-confirmed change. Against a real Postgres: a save
/// from a read another commit has overtaken is refused, so the second writer cannot silently put
/// back a rule the first just flipped.
/// </summary>
public class AlertPreferenceConcurrencyTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithCleanUp(true)
        .Build();

    private ServiceProvider _services = null!;

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
    public async Task AWriteFromAnOvertakenRead_IsRefused()
    {
        var memberId = Guid.NewGuid();
        using (var seed = _services.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
            db.AlertPreferences.Add(new AlertPreference { CardiMemberId = memberId, DisabledRules = "[\"activity_decline\"]" });
            await db.SaveChangesAsync();
        }

        using var firstScope = _services.CreateScope();
        using var secondScope = _services.CreateScope();
        var first = firstScope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
        var second = secondScope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();

        var seenByFirst = await first.AlertPreferences.SingleAsync(p => p.CardiMemberId == memberId);
        var seenBySecond = await second.AlertPreferences.SingleAsync(p => p.CardiMemberId == memberId);

        // The settings page switches a second rule off…
        seenBySecond.DisabledRules = "[\"activity_decline\",\"irregular_sleep\"]";
        await second.SaveChangesAsync();

        // …and a chat yes written from the older read must not put it back on.
        seenByFirst.DisabledRules = "[]";
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => first.SaveChangesAsync());

        using var readScope = _services.CreateScope();
        var stored = await readScope.ServiceProvider.GetRequiredService<CardiTrackDbContext>()
            .AlertPreferences.AsNoTracking().SingleAsync(p => p.CardiMemberId == memberId);
        Assert.Contains("irregular_sleep", stored.DisabledRules, StringComparison.Ordinal);
    }
}
