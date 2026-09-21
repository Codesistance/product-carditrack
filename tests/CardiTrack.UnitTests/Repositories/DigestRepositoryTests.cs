using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.UnitTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace CardiTrack.UnitTests.Repositories;

/// <summary>
/// Against the real PostgreSQL container, for the same reason as
/// <c>EnvironmentalReadingRepositoryTests</c>: the table is a partitioned parent created by raw
/// migration SQL and the insert is a hand-written <c>ON CONFLICT</c> statement naming its columns
/// one by one. A substitute vouches for neither, and it is precisely the column list that went
/// wrong — <c>Suggestions</c> reached the entity, the configuration and a migration without ever
/// reaching the <c>INSERT</c>, so a validated suggestion was dropped on the floor with no
/// exception and no log line, and the apps hid a section they had nothing to fill.
/// </summary>
[Collection("DatabaseCollection")]
public class DigestRepositoryTests(TestDatabaseFixture fixture)
{
    private static async Task EnsurePartitionsAsync(IServiceScope scope) =>
        await scope.ServiceProvider.GetRequiredService<ITimeSeriesPartitionService>()
            .EnsureUpcomingPartitionsAsync(daysAhead: 7);

    /// <summary>
    /// A member row for the digest to name. <c>DigestRepository</c> writes under
    /// <see cref="IMemberWriteGuard"/> since #1186, and the guard takes <c>FOR KEY SHARE</c> on the
    /// CardiMembers row before letting the write through — a random Guid has no row to lock, so
    /// the write is refused and nothing lands. That refusal is proven in
    /// <c>ErasureDuringGenerationTests</c>; what these tests are about is the column list, which
    /// needs the write to happen.
    /// </summary>
    private static async Task<Guid> SeedMemberAsync(IServiceScope scope)
    {
        var organization = await TestDataSeeder.SeedOrganizationAsync(scope);
        return (await TestDataSeeder.SeedCardiMemberAsync(scope, organization.Id)).Id;
    }

    private static DigestEntry Entry(Guid memberId, string? suggestion, DigestUrgency? urgency = null) => new()
    {
        CardiMemberId = memberId,
        LocalDate = DateOnly.FromDateTime(DateTime.UtcNow),
        Audience = DigestAudience.Family,
        Headline = "A settled night",
        Text = "Steps and heart rate both look ordinary today.",
        Suggestion = suggestion,
        Urgency = urgency,
        GeneratedAtUtc = DateTime.UtcNow,
    };

    [Fact]
    public async Task AddAsync_RoundTripsTheSuggestion()
    {
        using var scope = fixture.CreateScope();
        await EnsurePartitionsAsync(scope);
        var repo = scope.ServiceProvider.GetRequiredService<IDigestRepository>();
        var memberId = await SeedMemberAsync(scope);

        const string suggestion = "Ask how they slept when you call tonight";
        await repo.AddAsync(Entry(memberId, suggestion));

        var stored = await repo.GetLatestAsync(memberId, DigestAudience.Family);

        Assert.NotNull(stored);
        Assert.Equal(suggestion, stored.Suggestion);
    }

    /// <summary>
    /// The nullable case is its own test rather than an afterthought — the path that stores "no
    /// suggestion" is the one most likely to be missed by a hand-written column list.
    /// </summary>
    [Fact]
    public async Task AddAsync_StoresNoSuggestion_WhenTheGenerationProducedNone()
    {
        using var scope = fixture.CreateScope();
        await EnsurePartitionsAsync(scope);
        var repo = scope.ServiceProvider.GetRequiredService<IDigestRepository>();
        var memberId = await SeedMemberAsync(scope);

        await repo.AddAsync(Entry(memberId, suggestion: null));

        var stored = await repo.GetLatestAsync(memberId, DigestAudience.Family);

        Assert.NotNull(stored);
        Assert.Null(stored.Suggestion);
    }

    /// <summary>
    /// The rest of the column list, so the next column added by hand has something asserting the
    /// ones already there still arrive.
    /// </summary>
    [Fact]
    public async Task AddAsync_RoundTripsEveryOtherColumn()
    {
        using var scope = fixture.CreateScope();
        await EnsurePartitionsAsync(scope);
        var repo = scope.ServiceProvider.GetRequiredService<IDigestRepository>();
        var memberId = await SeedMemberAsync(scope);
        var entry = Entry(memberId, "a", DigestUrgency.CheckIn);

        await repo.AddAsync(entry);

        var stored = await repo.GetLatestAsync(memberId, DigestAudience.Family);

        Assert.NotNull(stored);
        Assert.Equal(entry.CardiMemberId, stored.CardiMemberId);
        Assert.Equal(entry.LocalDate, stored.LocalDate);
        Assert.Equal(entry.Audience, stored.Audience);
        Assert.Equal(entry.Headline, stored.Headline);
        Assert.Equal(entry.Text, stored.Text);
        Assert.Equal(entry.Urgency, stored.Urgency);
    }

    /// <summary>
    /// The nullable case for Urgency specifically: <c>Nullable&lt;T&gt;.ToString()</c> returns ""
    /// rather than null when unset, which would have inserted an empty string a later read could
    /// not parse back as the enum — the same class of bug this file exists to catch for
    /// Suggestion, at a different column.
    /// </summary>
    [Fact]
    public async Task AddAsync_StoresNoUrgency_WhenTheGenerationProducedNone()
    {
        using var scope = fixture.CreateScope();
        await EnsurePartitionsAsync(scope);
        var repo = scope.ServiceProvider.GetRequiredService<IDigestRepository>();
        var memberId = await SeedMemberAsync(scope);

        await repo.AddAsync(Entry(memberId, "a", urgency: null));

        var stored = await repo.GetLatestAsync(memberId, DigestAudience.Family);

        Assert.NotNull(stored);
        Assert.Null(stored.Urgency);
    }

    /// <summary>
    /// The insert is ON CONFLICT DO NOTHING. Callers that increment product counters after a
    /// write need to know whether a row landed, or a colliding run is counted as if it stored one.
    /// </summary>
    [Fact]
    public async Task AddAsync_ReturnsFalse_WhenTheSameGenerationIsInsertedTwice()
    {
        using var scope = fixture.CreateScope();
        await EnsurePartitionsAsync(scope);
        var repo = scope.ServiceProvider.GetRequiredService<IDigestRepository>();
        var entry = Entry(await SeedMemberAsync(scope), "a");

        Assert.True(await repo.AddAsync(entry));
        Assert.False(await repo.AddAsync(entry));
    }

    private static DigestEntry Daybook(Guid memberId, string headline, string text = "A quiet day, close to their usual.") => new()
    {
        CardiMemberId = memberId,
        LocalDate = DateOnly.FromDateTime(DateTime.UtcNow),
        Audience = DigestAudience.Daybook,
        Headline = headline,
        Text = text,
        GeneratedAtUtc = DateTime.UtcNow,
        PromptVersion = 1,
    };

    /// <summary>
    /// The replacement behind a caregiver's rewrite: the earlier book goes and the new one takes
    /// its date, in one call — against the partitioned table, the partial unique index and the
    /// hand-written insert, none of which a substitute can vouch for.
    /// </summary>
    [Fact]
    public async Task ReplaceBookAsync_SwapsTheBookForThePeriod()
    {
        using var scope = fixture.CreateScope();
        await EnsurePartitionsAsync(scope);
        var repo = scope.ServiceProvider.GetRequiredService<IDigestRepository>();
        var memberId = await SeedMemberAsync(scope);
        Assert.True(await repo.AddAsync(Daybook(memberId, "The first account")));

        var (removed, inserted) = await repo.ReplaceBookAsync(Daybook(memberId, "The second account"));

        Assert.Equal(1, removed);
        Assert.True(inserted);
        var stored = await repo.GetLatestByDateAsync(memberId, DateOnly.FromDateTime(DateTime.UtcNow), DigestAudience.Daybook);
        Assert.NotNull(stored);
        Assert.Equal("The second account", stored.Headline);
    }

    /// <summary>
    /// The guarantee the transaction exists for: when the insert fails after the delete has run,
    /// the delete is rolled back and the caregiver still has the book they had. A null text trips
    /// the column's NOT NULL, which is the cheapest failure that happens strictly after the delete.
    /// </summary>
    [Fact]
    public async Task ReplaceBookAsync_KeepsTheOldBook_WhenTheNewOneCannotBeStored()
    {
        using var scope = fixture.CreateScope();
        await EnsurePartitionsAsync(scope);
        var repo = scope.ServiceProvider.GetRequiredService<IDigestRepository>();
        var memberId = await SeedMemberAsync(scope);
        Assert.True(await repo.AddAsync(Daybook(memberId, "The account that must survive")));

        await Assert.ThrowsAnyAsync<Exception>(() =>
            repo.ReplaceBookAsync(Daybook(memberId, "Never stored", text: null!)));

        var stored = await repo.GetLatestByDateAsync(memberId, DateOnly.FromDateTime(DateTime.UtcNow), DigestAudience.Daybook);
        Assert.NotNull(stored);
        Assert.Equal("The account that must survive", stored.Headline);
    }

    [Fact]
    public async Task DeleteBookAsync_RemovesOneJournalBook_AndRefusesTheFamilySeries()
    {
        using var scope = fixture.CreateScope();
        await EnsurePartitionsAsync(scope);
        var repo = scope.ServiceProvider.GetRequiredService<IDigestRepository>();
        var memberId = await SeedMemberAsync(scope);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        Assert.True(await repo.AddAsync(Daybook(memberId, "To be deleted")));
        Assert.True(await repo.AddAsync(Entry(memberId, suggestion: null)));

        var removed = await repo.DeleteBookAsync(memberId, today, DigestAudience.Daybook);

        Assert.Equal(1, removed);
        Assert.Null(await repo.GetLatestByDateAsync(memberId, today, DigestAudience.Daybook));
        Assert.NotNull(await repo.GetLatestByDateAsync(memberId, today, DigestAudience.Family));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            repo.DeleteBookAsync(memberId, today, DigestAudience.Family));
    }
}
