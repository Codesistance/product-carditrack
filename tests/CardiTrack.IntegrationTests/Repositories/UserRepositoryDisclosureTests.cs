using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Persistence;
using CardiTrack.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace CardiTrack.IntegrationTests.Repositories;

/// <summary>
/// The first acknowledgement of the health-data disclosure is the compliance record, and two
/// devices can acknowledge at once. Whether the second one can overwrite the first is a question
/// of the SQL that runs, not of the C# around it — so this runs against a real Postgres.
/// </summary>
public class UserRepositoryDisclosureTests : IAsyncLifetime
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
    public async Task TheFirstDismissalIsRecorded_AndASecondCannotOverwriteIt()
    {
        var auth0Id = $"auth0|{Guid.NewGuid():N}";
        await SeedUserAsync(auth0Id);
        var first = new DateTime(2026, 9, 13, 10, 0, 0, DateTimeKind.Utc);
        var later = first.AddHours(1);

        bool firstRecorded, secondRecorded;
        using (var scope = _services.CreateScope())
        {
            var repo = new UserRepository(scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>());
            firstRecorded = await repo.TryRecordHealthDataDisclosureDismissalAsync(auth0Id, first);
            secondRecorded = await repo.TryRecordHealthDataDisclosureDismissalAsync(auth0Id, later);
        }

        Assert.True(firstRecorded);
        Assert.False(secondRecorded);

        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
            var stored = await db.Users.AsNoTracking().SingleAsync(u => u.Auth0UserId == auth0Id);
            Assert.Equal(first, stored.HealthDataDisclosureDismissedDate);
        }
    }

    [Fact]
    public async Task NothingIsRecorded_ForAnIdentityWithNoUserRow()
    {
        using var scope = _services.CreateScope();
        var repo = new UserRepository(scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>());

        Assert.False(await repo.TryRecordHealthDataDisclosureDismissalAsync("auth0|nobody", DateTime.UtcNow));
    }

    private async Task SeedUserAsync(string auth0Id)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
        var organization = new Organization { Name = "Doe family", Type = OrganizationType.Family };
        db.Organizations.Add(organization);
        await db.SaveChangesAsync();
        db.Users.Add(new User
        {
            OrganizationId = organization.Id,
            Auth0UserId = auth0Id,
            Email = $"caregiver-{Guid.NewGuid():N}@example.com",
            Name = "Jane Doe",
        });
        await db.SaveChangesAsync();
    }
}
