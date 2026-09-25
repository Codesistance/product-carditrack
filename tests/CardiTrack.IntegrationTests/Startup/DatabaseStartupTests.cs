using CardiTrack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;

namespace CardiTrack.IntegrationTests.Startup;

/// <summary>
/// Verifies that migrations apply cleanly and the expected schema is in place.
/// Each test gets an isolated container so failures are independent.
/// </summary>
public class DatabaseStartupTests : IAsyncLifetime
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
    public async Task Migrations_ApplyWithoutErrors()
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();

        var exception = await Record.ExceptionAsync(() => db.Database.MigrateAsync());

        Assert.Null(exception);
    }

    [Fact]
    public async Task Migrations_AreAllApplied()
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
        await db.Database.MigrateAsync();

        var pending = await db.Database.GetPendingMigrationsAsync();

        Assert.Empty(pending);
    }

    [Fact]
    public async Task MigrationHistory_IsTracked()
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
        await db.Database.MigrateAsync();

        var applied = await db.Database.GetAppliedMigrationsAsync();

        Assert.NotEmpty(applied);
    }

    [Theory]
    [InlineData("ActivityLogs")]
    [InlineData("Alerts")]
    [InlineData("AuditLogs")]
    [InlineData("CardiMembers")]
    [InlineData("DeviceConnections")]
    [InlineData("Devices")]
    [InlineData("Organizations")]
    [InlineData("PatternBaselines")]
    [InlineData("Subscriptions")]
    [InlineData("UserCardiMembers")]
    [InlineData("Users")]
    public async Task ExpectedTable_ExistsAfterMigration(string tableName)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
        await db.Database.MigrateAsync();

        await using var conn = new NpgsqlConnection(_container.GetConnectionString());
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(1) FROM information_schema.tables
            WHERE table_schema = 'public' AND table_name = @name
            """;
        cmd.Parameters.AddWithValue("name", tableName);

        var count = (long)(await cmd.ExecuteScalarAsync())!;
        Assert.True(count == 1, $"Table '{tableName}' was not found in the database after migration.");
    }

    [Fact]
    public async Task Migrations_AreIdempotent_WhenRunTwice()
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();

        await db.Database.MigrateAsync();

        var exception = await Record.ExceptionAsync(() => db.Database.MigrateAsync());

        Assert.Null(exception);
    }
}
