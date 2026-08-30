using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Persistence;
using CardiTrack.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace CardiTrack.IntegrationTests.Startup;

/// <summary>
/// What the CardiJournal's books can actually be stored as, against a real partitioned
/// <c>DigestEntries</c> — the half of the write path the generator's unit tests substitute away.
/// </summary>
/// <remarks>
/// Both facts pinned here are ones the generator depends on and cannot see: that a Weekbook may
/// land on a date that already carries that member's Daybook (they share a <c>LocalDate</c> most
/// weeks, which is why each audience has its own partial unique index), and how many characters
/// the account column holds — the number <c>DigestGenerationService.MaxTextLength</c> guards
/// against, and which nothing else stops from drifting away from the DDL.
/// </remarks>
public class DigestEntryStorageTests : IAsyncLifetime
{
    /// <summary>The <c>varchar</c> the account is stored in — see <c>DigestEntryConfiguration</c>.</summary>
    private const int TextColumnLength = 4000;

    private static readonly DateOnly WeekEnd = new(2026, 8, 9);

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithCleanUp(true)
        .Build();

    private CardiTrackDbContext _db = null!;
    private DigestRepository _digests = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        var options = new DbContextOptionsBuilder<CardiTrackDbContext>()
            .UseNpgsql(_container.GetConnectionString(),
                b => b.MigrationsAssembly("CardiTrack.Infrastructure"))
            .Options;

        _db = new CardiTrackDbContext(options);
        await _db.Database.MigrateAsync();

        // The month's partition, which PartitionMaintenanceWorker creates ahead of need in a
        // running environment and no migration bakes in.
        await _db.Database.ExecuteSqlRawAsync(
            TimeSeriesPartitions.CreateDigestPartitionSql(new DateOnly(WeekEnd.Year, WeekEnd.Month, 1)));

        _digests = new DigestRepository(_db);
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _container.DisposeAsync();
    }

    /// <summary>
    /// The Weekbook is dated by its week's last day, which is also a day with its own Daybook. One
    /// unique index across both audiences would have refused this second write, and the generator
    /// would never have been told: the insert absorbs conflicts with a bare DO NOTHING.
    /// </summary>
    [Fact]
    public async Task A_weekbook_lands_on_a_date_that_already_carries_a_daybook()
    {
        var member = Guid.NewGuid();

        await _digests.AddAsync(Entry(member, DigestAudience.Daybook, At(1, 0)));

        // What the Weekbook's own due-check probes. It must not see the Daybook just written.
        Assert.Null(await _digests.GetLatestByDateAsync(member, WeekEnd, DigestAudience.Weekbook));

        await _digests.AddAsync(Entry(member, DigestAudience.Weekbook, At(1, 5)));

        var written = await _digests.GetLatestByDateAsync(member, WeekEnd, DigestAudience.Weekbook);
        Assert.NotNull(written);
        Assert.Equal(DigestAudience.Weekbook, written!.Audience);
        Assert.Equal(2, await CountFor(member));
    }

    /// <summary>
    /// Two overlapping executions both probe "already written?" before either writes. The partial
    /// unique index is what holds written-once, and the second insert must be a quiet no-op.
    /// </summary>
    [Fact]
    public async Task A_second_weekbook_for_the_same_week_is_absorbed()
    {
        var member = Guid.NewGuid();

        await _digests.AddAsync(Entry(member, DigestAudience.Weekbook, At(1, 0)));
        await _digests.AddAsync(Entry(member, DigestAudience.Weekbook, At(1, 30)));

        Assert.Equal(1, await CountFor(member));
    }

    /// <summary>
    /// The column boundary <c>DigestGenerationService.MaxTextLength</c> is written against. A book
    /// past it used to reach the insert and throw, which the pass caught as "generation failed" —
    /// and because a book is written once, that period was gone.
    /// </summary>
    [Fact]
    public async Task The_account_column_holds_exactly_the_length_the_generator_guards()
    {
        var member = Guid.NewGuid();

        await _digests.AddAsync(Entry(member, DigestAudience.Weekbook, At(1, 0), new string('a', TextColumnLength)));
        Assert.Equal(1, await CountFor(member));

        var overflowed = await Record.ExceptionAsync(() => _digests.AddAsync(
            Entry(Guid.NewGuid(), DigestAudience.Weekbook, At(1, 0), new string('a', TextColumnLength + 1))));

        var postgres = Assert.IsType<PostgresException>(overflowed);
        Assert.Equal("22001", postgres.SqlState); // string_data_right_truncation
    }

    private async Task<int> CountFor(Guid member) =>
        await _db.DigestEntries.AsNoTracking().CountAsync(d => d.CardiMemberId == member);

    private static DateTime At(int hour, int minute) =>
        new(2026, 8, 10, hour, minute, 0, DateTimeKind.Utc);

    private static DigestEntry Entry(
        Guid member, DigestAudience audience, DateTime generatedAtUtc, string? text = null) => new()
    {
        CardiMemberId = member,
        LocalDate = WeekEnd,
        Audience = audience,
        Headline = "A steadier week for sleep",
        Text = text ?? "An account of the period, as the model wrote it.",
        Suggestion = null,
        Urgency = null,
        GeneratedAtUtc = generatedAtUtc,
    };
}
