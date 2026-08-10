using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Persistence;
using CardiTrack.UnitTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace CardiTrack.UnitTests.Repositories;

/// <summary>
/// Against the real PostgreSQL container rather than a substitute, because the thing under test is
/// the SQL: <see cref="Notification.Priority"/> is persisted as text, so ordering by the column
/// sorts alphabetically — Critical, High, <b>Low</b>, Medium — and no in-memory provider would
/// reproduce that. The ranking is what decides which two cards a caregiver sees on the dashboard.
/// </summary>
[Collection("DatabaseCollection")]
public class NotificationRepositoryTests(TestDatabaseFixture fixture)
{
    private static readonly DateTime Detected = new(2026, 8, 10, 6, 0, 0, DateTimeKind.Utc);

    private static Notification Row(
        Guid userId, NotificationPriority priority, string ruleCode, bool isOwner = true) => new()
        {
            OrganizationId = Guid.NewGuid(),
            UserId = userId,
            RuleCode = ruleCode,
            RuleVersion = 1,
            Category = NotificationCategory.Blocking,
            Priority = priority,
            // Unique per row: the table has a unique index on it.
            Fingerprint = $"{userId:N}-{ruleCode}",
            TitleKey = $"nudge.{ruleCode}.title",
            BodyKey = $"nudge.{ruleCode}.body",
            BenefitKey = $"nudge.{ruleCode}.benefit",
            TemplateData = "{}",
            ActionDeepLink = "carditrack://settings",
            State = NotificationState.Open,
            IsOwner = isOwner,
            FirstDetectedDate = Detected,
            LastEvaluatedDate = Detected
        };

    /// <summary>
    /// Seeds one row per priority, deliberately inserted in an order that neither matches urgency
    /// nor the alphabet, so a query that happened to return insertion order would fail too.
    /// </summary>
    private static async Task<Guid> SeedOneOfEachPriorityAsync(IServiceScope scope)
    {
        var userId = Guid.NewGuid();
        var context = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();

        context.Set<Notification>().AddRange(
            Row(userId, NotificationPriority.Medium, "PAUSE_LEFT_LONG"),
            Row(userId, NotificationPriority.Low, "MEDICAL_NOTES_EMPTY"),
            Row(userId, NotificationPriority.Critical, "DEVICE_REMOVED"),
            Row(userId, NotificationPriority.High, "TIMEZONE_DEFAULT"));

        await context.SaveChangesAsync();
        return userId;
    }

    private static readonly NotificationPriority[] ByUrgency =
    [
        NotificationPriority.Critical,
        NotificationPriority.High,
        NotificationPriority.Medium,
        NotificationPriority.Low
    ];

    [Fact]
    public async Task QueryAsync_RanksByUrgency_NotByThePriorityColumnsText()
    {
        using var scope = fixture.CreateScope();
        var userId = await SeedOneOfEachPriorityAsync(scope);
        var repo = scope.ServiceProvider.GetRequiredService<INotificationRepository>();

        var rows = await repo.QueryAsync(
            userId, state: null, category: null, cardiMemberId: null, owned: null,
            limit: 10, offset: 0);

        Assert.Equal(ByUrgency, rows.Select(r => r.Priority));
    }

    /// <summary>
    /// The dashboard shows the top two. Alphabetical ordering would put Low in that second slot
    /// ahead of Medium, so this asserts the slot contents rather than only the full sequence.
    /// </summary>
    [Fact]
    public async Task GetTopForDashboardAsync_TakesTheMostUrgent_NotTheAlphabeticallyFirst()
    {
        using var scope = fixture.CreateScope();
        var userId = await SeedOneOfEachPriorityAsync(scope);
        var repo = scope.ServiceProvider.GetRequiredService<INotificationRepository>();

        var rows = await repo.GetTopForDashboardAsync(userId, limit: 2, utcNow: Detected.AddDays(1));

        Assert.Equal(
            [NotificationPriority.Critical, NotificationPriority.High],
            rows.Select(r => r.Priority));
    }

    /// <summary>
    /// Paging has to stay consistent with the ranking, or the second page repeats what the first
    /// already showed. Taking two pages of two must reproduce the single ordered list.
    /// </summary>
    [Fact]
    public async Task QueryAsync_PagesInTheSameOrderItRanks()
    {
        using var scope = fixture.CreateScope();
        var userId = await SeedOneOfEachPriorityAsync(scope);
        var repo = scope.ServiceProvider.GetRequiredService<INotificationRepository>();

        var first = await repo.QueryAsync(userId, null, null, null, null, limit: 2, offset: 0);
        var second = await repo.QueryAsync(userId, null, null, null, null, limit: 2, offset: 2);

        Assert.Equal(ByUrgency, first.Concat(second).Select(r => r.Priority));
    }
}
