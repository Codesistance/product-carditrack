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

    // ── The dispatch worker's push sweep ──────────────────────────────────────

    /// <summary>
    /// Every state the sweep must exclude, seeded together. Each exclusion is a different way for a
    /// caregiver's phone to ring when it should not, and the filtered index this rides on makes the
    /// predicate worth pinning against real SQL rather than a substitute's say-so.
    /// </summary>
    [Fact]
    public async Task GetPendingPushAsync_ReturnsOnlyOpenUnpushedOwnedRowsForTheGivenRules()
    {
        using var scope = fixture.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
        var userId = Guid.NewGuid();

        var wanted = Row(userId, NotificationPriority.Critical, "DEVICE_BATTERY_LOW");

        var alreadyPushed = Row(userId, NotificationPriority.Critical, "DEVICE_AUTH_BROKEN");
        alreadyPushed.PushedDate = Detected;

        // A snooze the caregiver chose is not a state to push through.
        var snoozed = Row(userId, NotificationPriority.Critical, "DEVICE_BATTERY_LOW");
        snoozed.Fingerprint = $"{userId:N}-snoozed";
        snoozed.State = NotificationState.Snoozed;
        snoozed.SnoozedUntil = Detected.AddHours(12);

        var resolved = Row(userId, NotificationPriority.Critical, "DEVICE_BATTERY_LOW");
        resolved.Fingerprint = $"{userId:N}-resolved";
        resolved.State = NotificationState.Resolved;

        // The read-only copy a second caregiver holds — visible, never a second ringing phone.
        var notOwned = Row(userId, NotificationPriority.Critical, "DEVICE_BATTERY_LOW", isOwner: false);
        notOwned.Fingerprint = $"{userId:N}-not-owned";

        // Open and unpushed, but its rule does not declare PushesWhenOpen.
        var notPushableRule = Row(userId, NotificationPriority.High, "TIMEZONE_DEFAULT");

        context.Set<Notification>().AddRange(
            wanted, alreadyPushed, snoozed, resolved, notOwned, notPushableRule);
        await context.SaveChangesAsync();

        var repo = scope.ServiceProvider.GetRequiredService<INotificationRepository>();

        var pending = await repo.GetPendingPushAsync(
            ["DEVICE_BATTERY_LOW", "DEVICE_AUTH_BROKEN"], limit: 50);

        var row = Assert.Single(pending, n => n.UserId == userId);
        Assert.Equal(wanted.Fingerprint, row.Fingerprint);
    }

    /// <summary>
    /// The claim is what keeps three Cloud Run instances from sending three pushes for one flat
    /// battery, and it only works because the database arbitrates it — which makes this exactly the
    /// test that must run against real Postgres rather than a substitute agreeing with itself.
    /// </summary>
    [Fact]
    public async Task TryClaimForPushAsync_IsWonByExactlyOneCaller()
    {
        using var scope = fixture.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
        var row = Row(Guid.NewGuid(), NotificationPriority.Critical, "DEVICE_BATTERY_LOW");
        context.Set<Notification>().Add(row);
        await context.SaveChangesAsync();

        var repo = scope.ServiceProvider.GetRequiredService<INotificationRepository>();

        var first = await repo.TryClaimForPushAsync(row.Id, Detected);
        var second = await repo.TryClaimForPushAsync(row.Id, Detected.AddSeconds(30));

        Assert.True(first);
        Assert.False(second);
    }

    [Fact]
    public async Task TryClaimForPushAsync_RefusesARowThatStoppedQualifying()
    {
        // The window between the sweep's read and its claim. A nudge dismissed in it must not push.
        using var scope = fixture.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
        var row = Row(Guid.NewGuid(), NotificationPriority.Critical, "DEVICE_BATTERY_LOW");
        row.State = NotificationState.Resolved;
        context.Set<Notification>().Add(row);
        await context.SaveChangesAsync();

        var repo = scope.ServiceProvider.GetRequiredService<INotificationRepository>();

        Assert.False(await repo.TryClaimForPushAsync(row.Id, Detected));
    }

    [Fact]
    public async Task ReleasePushClaimAsync_PutsTheRowBackInThePendingSet()
    {
        // The failed-enqueue path: a claim held by a send that never happened would mark the
        // warning delivered and the sweep would never look at the row again.
        using var scope = fixture.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
        var userId = Guid.NewGuid();
        var row = Row(userId, NotificationPriority.Critical, "DEVICE_BATTERY_LOW");
        context.Set<Notification>().Add(row);
        await context.SaveChangesAsync();

        var repo = scope.ServiceProvider.GetRequiredService<INotificationRepository>();
        Assert.True(await repo.TryClaimForPushAsync(row.Id, Detected));
        Assert.DoesNotContain(
            await repo.GetPendingPushAsync(["DEVICE_BATTERY_LOW"], limit: 50),
            n => n.Id == row.Id);

        await repo.ReleasePushClaimAsync(row.Id);

        Assert.Contains(
            await repo.GetPendingPushAsync(["DEVICE_BATTERY_LOW"], limit: 50),
            n => n.Id == row.Id);
        Assert.True(await repo.TryClaimForPushAsync(row.Id, Detected.AddMinutes(1)));
    }

    [Fact]
    public async Task GetPendingPushAsync_ReturnsNothing_WhenNoRulesDeclareThatTheyPush()
    {
        // Defends the sweep against an empty catalogue filter degenerating into "every open row".
        using var scope = fixture.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
        var userId = Guid.NewGuid();

        context.Set<Notification>().Add(Row(userId, NotificationPriority.Critical, "DEVICE_BATTERY_LOW"));
        await context.SaveChangesAsync();

        var repo = scope.ServiceProvider.GetRequiredService<INotificationRepository>();

        Assert.Empty(await repo.GetPendingPushAsync([], limit: 50));
    }
}
