using CardiTrack.Domain.Common;
using CardiTrack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace CardiTrack.IntegrationTests.Repositories;

/// <summary>
/// The <c>SplitCardiMemberName</c> backfill runs once, against production rows nobody can look at
/// first, so it is proved here on a real Postgres: members stored under the old single name are
/// migrated through it, and every one must come out exactly as <see cref="PersonName.Split"/> —
/// the rule a legacy client's single name goes through today — would split it.
/// </summary>
public class SplitCardiMemberNameMigrationTests : IAsyncLifetime
{
    private const string MigrationBefore = "20260925150145_AddPendingGrantRevocations";
    private const string MigrationUnderTest = "20260925171252_SplitCardiMemberName";

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

    private static readonly string[] LegacyNames =
    [
        "Arthur Doe",
        "Arthur",
        "Mary Ann Smith",
        "  Padded   Name  ",
        "Tab\tSeparated",
        "Line\nBreak Name",
        "Jean-Luc Picard",
        "Zoë O'Brien",
    ];

    /// <summary>All names in one migration pass — one container rather than one per name.</summary>
    [Fact]
    public async Task Backfill_SplitsEveryStoredNameExactlyAsPersonNameSplitDoes()
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
        var migrator = db.GetService<IMigrator>();

        await migrator.MigrateAsync(MigrationBefore);
        var ids = new List<(Guid Id, string Legacy)>();
        foreach (var name in LegacyNames)
            ids.Add((await InsertLegacyMemberAsync(db, name), name));

        await migrator.MigrateAsync(MigrationUnderTest);

        foreach (var (id, legacy) in ids)
        {
            var (first, last) = await ReadNamesAsync(db, id);
            var expected = PersonName.Split(legacy);
            Assert.True(
                expected.FirstName == first && expected.LastName == last,
                $"'{legacy}' backfilled as ('{first}', '{last}'), expected ('{expected.FirstName}', '{expected.LastName}')");
        }
    }

    [Fact]
    public async Task Down_RejoinsTheParts()
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
        var migrator = db.GetService<IMigrator>();

        await migrator.MigrateAsync(MigrationBefore);
        var single = await InsertLegacyMemberAsync(db, "Arthur");
        var full = await InsertLegacyMemberAsync(db, "Mary Ann Smith");

        await migrator.MigrateAsync(MigrationUnderTest);
        await migrator.MigrateAsync(MigrationBefore);

        Assert.Equal("Arthur", await ReadLegacyNameAsync(db, single));
        Assert.Equal("Mary Ann Smith", await ReadLegacyNameAsync(db, full));
    }

    /// <summary>
    /// Raw SQL against the pre-split schema — the entity no longer has a <c>Name</c> to write
    /// through. Foreign keys are switched off for the insert only: the backfill reads nothing but
    /// the name, so an organization row would be scaffolding the assertion never looks at.
    /// </summary>
    private static async Task<Guid> InsertLegacyMemberAsync(CardiTrackDbContext db, string name)
    {
        var id = Guid.NewGuid();
        var organizationId = Guid.NewGuid();
        await db.Database.OpenConnectionAsync();
        try
        {
            await db.Database.ExecuteSqlRawAsync("SET session_replication_role = replica");
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "CardiMembers" ("Id", "OrganizationId", "Name", "DateOfBirth", "Gender", "AlertSensitivity")
                VALUES ({id}, {organizationId}, {name}, {new DateOnly(1950, 1, 1)}, 'Female', 'Medium')
                """);
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("SET session_replication_role = DEFAULT");
            await db.Database.CloseConnectionAsync();
        }
        return id;
    }

    private static async Task<(string First, string? Last)> ReadNamesAsync(CardiTrackDbContext db, Guid id)
    {
        var row = await db.Database
            .SqlQuery<NameRow>($"""SELECT "FirstName", "LastName" FROM "CardiMembers" WHERE "Id" = {id}""")
            .SingleAsync();
        return (row.FirstName, row.LastName);
    }

    private static Task<string> ReadLegacyNameAsync(CardiTrackDbContext db, Guid id) =>
        db.Database
            .SqlQuery<string>($"""SELECT "Name" AS "Value" FROM "CardiMembers" WHERE "Id" = {id}""")
            .SingleAsync();

    private sealed record NameRow(string FirstName, string? LastName);
}
