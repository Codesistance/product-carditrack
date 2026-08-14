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

    private static DigestEntry Entry(Guid memberId, string? suggestion) => new()
    {
        CardiMemberId = memberId,
        LocalDate = DateOnly.FromDateTime(DateTime.UtcNow),
        Audience = DigestAudience.Family,
        Headline = "A settled night",
        Text = "Steps and heart rate both look ordinary today.",
        Suggestion = suggestion,
        GeneratedAtUtc = DateTime.UtcNow,
    };

    [Fact]
    public async Task AddAsync_RoundTripsTheSuggestion()
    {
        using var scope = fixture.CreateScope();
        await EnsurePartitionsAsync(scope);
        var repo = scope.ServiceProvider.GetRequiredService<IDigestRepository>();
        var memberId = Guid.NewGuid();

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
        var memberId = Guid.NewGuid();

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
        var memberId = Guid.NewGuid();
        var entry = Entry(memberId, "a");

        await repo.AddAsync(entry);

        var stored = await repo.GetLatestAsync(memberId, DigestAudience.Family);

        Assert.NotNull(stored);
        Assert.Equal(entry.CardiMemberId, stored.CardiMemberId);
        Assert.Equal(entry.LocalDate, stored.LocalDate);
        Assert.Equal(entry.Audience, stored.Audience);
        Assert.Equal(entry.Headline, stored.Headline);
        Assert.Equal(entry.Text, stored.Text);
    }
}
