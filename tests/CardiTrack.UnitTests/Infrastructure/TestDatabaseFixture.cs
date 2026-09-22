using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Services;
using CardiTrack.Infrastructure.Persistence;
using CardiTrack.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace CardiTrack.UnitTests.Infrastructure;

public class TestDatabaseFixture : IAsyncLifetime
{
    private PostgreSqlContainer _container = null!;
    private ServiceProvider _serviceProvider = null!;

    public async Task InitializeAsync()
    {
        _container = PostgreSqlTestContainerFactory.CreateStandardContainer();
        await _container.StartAsync();

        var services = new ServiceCollection();

        services.AddDbContext<CardiTrackDbContext>(options =>
            options.UseNpgsql(_container.GetConnectionString())
                   .EnableDetailedErrors()
                   .EnableSensitiveDataLogging());

        // UnitOfWork takes every repository, and they all construct from the DbContext (plus, for
        // a few, the write guard below). Registering each constructor parameter's interface against
        // the one Infrastructure class implementing it keeps this fixture from being a hand-written
        // list that silently breaks every test in the project the next time a repository is added —
        // which is exactly what it did twice while family sharing was being built.
        var infrastructure = typeof(UnitOfWork).Assembly;
        foreach (var parameter in typeof(UnitOfWork).GetConstructors().Single().GetParameters())
        {
            if (parameter.ParameterType == typeof(CardiTrackDbContext))
                continue;

            var implementation = infrastructure.GetTypes().Single(t =>
                t.IsClass && !t.IsAbstract && parameter.ParameterType.IsAssignableFrom(t));
            services.AddScoped(parameter.ParameterType, implementation);
        }

        // Not UnitOfWork constructor parameters, so the loop above never reaches them.
        //
        // The write guard is a pass-through here: the real one takes a row lock on CardiMembers,
        // and these tests deliberately write for member ids they never seeded — a real guard would
        // refuse every one of them. What the guard actually does is proven in
        // ErasureDuringGenerationTests against a real Postgres.
        services.AddScoped<IMemberWriteGuard, CardiTrack.UnitTests.Services.PassThroughWriteGuard>();
        services.AddScoped<IActivityLogAggregationService, ActivityLogAggregationService>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<ITimeSeriesPartitionService, TimeSeriesPartitionService>();
        services.AddLogging();

        _serviceProvider = services.BuildServiceProvider();

        using var scope = _serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
        await context.Database.MigrateAsync();
    }

    /// <summary>Creates a new DI scope. Each test should use its own scope.</summary>
    public IServiceScope CreateScope() => _serviceProvider.CreateScope();

    public async Task DisposeAsync()
    {
        await _serviceProvider.DisposeAsync();
        await _container.DisposeAsync();
    }
}
