using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Persistence;
using CardiTrack.Infrastructure.Repositories;
using CardiTrack.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CardiTrack.IntegrationTests.Services;

/// <summary>
/// The two date boundaries <c>RetentionWorker</c> enforces, tested where they are decided rather
/// than through the worker that schedules them.
/// </summary>
/// <remarks>
/// Both are published figures — thirty days to erase an account, ninety days to keep a
/// conversation — so both fail in a way somebody outside this repository would notice. Both are
/// also SQL over timestamps, which is the one kind of logic a substitute cannot check: the
/// correlated "newest turn" aggregate either translates or it does not, and a test against an
/// in-memory list would pass either way.
/// </remarks>
public class RetentionPolicyTests : IAsyncLifetime
{
    private readonly Testcontainers.PostgreSql.PostgreSqlContainer _container =
        new Testcontainers.PostgreSql.PostgreSqlBuilder("postgres:17-alpine")
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
        sc.AddLogging();
        _services = sc.BuildServiceProvider();

        using var scope = _services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>().Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _container.DisposeAsync();
    }

    // ---- The thirty-day account window -------------------------------------------------------

    /// <summary>
    /// The published promise is deletion <em>within</em> thirty days of the request, so an
    /// account one day past its window is due — and one a day short of it is not. Getting this
    /// backwards erases an account while its owner still has the right to keep it.
    /// </summary>
    [Fact]
    public async Task AccountsPastTheCancellationWindow_AreDue_AndOnesInsideItAreNot()
    {
        var organizationId = await SeedOrganizationAsync();
        var expired = await SeedUserAsync(organizationId, requestedDaysAgo: 31);
        var stillCancellable = await SeedUserAsync(organizationId, requestedDaysAgo: 29);
        var neverAsked = await SeedUserAsync(organizationId, requestedDaysAgo: null);

        var due = await FindDueAsync(limit: 50);

        Assert.Contains(expired, due);
        Assert.DoesNotContain(stillCancellable, due);
        Assert.DoesNotContain(neverAsked, due);
    }

    /// <summary>
    /// A bounded run must take the account that has been waiting longest, or a backlog larger
    /// than the batch would starve the oldest request indefinitely — which is the one that is
    /// closest to breaking the published window.
    /// </summary>
    [Fact]
    public async Task DueAccounts_ComeBackOldestRequestFirst_AndRespectTheBatchSize()
    {
        var organizationId = await SeedOrganizationAsync();
        var oldest = await SeedUserAsync(organizationId, requestedDaysAgo: 90);
        await SeedUserAsync(organizationId, requestedDaysAgo: 60);
        await SeedUserAsync(organizationId, requestedDaysAgo: 31);

        var due = await FindDueAsync(limit: 2);

        Assert.Equal(2, due.Count);
        Assert.Equal(oldest, due[0]);
    }

    /// <summary>
    /// The threshold the worker applies is <see cref="UserService.DeletionGracePeriod"/>, the
    /// same constant the API quotes back to a caregiver. This is the assertion that keeps the two
    /// from drifting: change the constant and the sentence in the app changes with it, but a
    /// second copy of "30" inside a query would not.
    /// </summary>
    [Fact]
    public void TheGracePeriodTheWorkerUses_IsTheOneTheAppPromises()
    {
        Assert.Equal(TimeSpan.FromDays(30), UserService.DeletionGracePeriod);
    }

    // ---- The ninety-day chat window ----------------------------------------------------------

    /// <summary>
    /// A conversation is as old as its newest turn. The case that matters is the second one: a
    /// thread started six months ago and answered yesterday is a live conversation, and trimming
    /// its early turns would leave a caregiver reading answers to questions that are gone.
    /// </summary>
    [Fact]
    public async Task ConversationsExpireOnTheirNewestTurn_NotOnWhenTheyStarted()
    {
        var (_, userId, memberId) = await SeedMemberAsync();
        var stale = await SeedSessionAsync(userId, memberId,
            startedDaysAgo: 120, turnDaysAgo: [120, 100]);
        var revived = await SeedSessionAsync(userId, memberId,
            startedDaysAgo: 180, turnDaysAgo: [180, 1]);

        var expired = await FindExpiredSessionsAsync(cutoffDaysAgo: 90, limit: 50);

        Assert.Contains(stale, expired);
        Assert.DoesNotContain(revived, expired);
    }

    /// <summary>
    /// A session opened and never used has no turns to be dated from. Without the fallback to
    /// <c>StartedAtUtc</c> it would have no age at all and would sit in the table forever — the
    /// quiet way a retention period stops being kept.
    /// </summary>
    [Fact]
    public async Task AConversationWithNoTurns_ExpiresFromWhenItWasStarted()
    {
        var (_, userId, memberId) = await SeedMemberAsync();
        var abandoned = await SeedSessionAsync(userId, memberId, startedDaysAgo: 120, turnDaysAgo: []);
        var recent = await SeedSessionAsync(userId, memberId, startedDaysAgo: 10, turnDaysAgo: []);

        var expired = await FindExpiredSessionsAsync(cutoffDaysAgo: 90, limit: 50);

        Assert.Contains(abandoned, expired);
        Assert.DoesNotContain(recent, expired);
    }

    /// <summary>
    /// Whole conversations (#488): the turns and their per-call usage rows go with the session,
    /// and nothing of the live conversation next to it is touched.
    /// </summary>
    [Fact]
    public async Task DeletingAConversation_TakesItsTurnsAndUsageRows_AndLeavesTheLiveOneAlone()
    {
        var (_, userId, memberId) = await SeedMemberAsync();
        var stale = await SeedSessionAsync(userId, memberId, startedDaysAgo: 120, turnDaysAgo: [120, 100]);
        var live = await SeedSessionAsync(userId, memberId, startedDaysAgo: 5, turnDaysAgo: [5, 1]);

        var expired = await FindExpiredSessionsAsync(cutoffDaysAgo: 90, limit: 50);
        Assert.Equal([stale], expired);

        var report = await DeleteSessionsAsync(expired);

        Assert.Equal(1, report.Sessions);
        Assert.Equal(2, report.Turns);
        Assert.Equal(2, report.Usages);

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();

        Assert.Equal(0, await db.MemberChatSessions.CountAsync(s => s.Id == stale));
        Assert.Equal(0, await db.MemberChatTurns.CountAsync(t => t.SessionId == stale));

        // Emptiness proves nothing if the neighbour was never there: the live conversation and
        // everything under it must survive the same sweep.
        Assert.Equal(1, await db.MemberChatSessions.CountAsync(s => s.Id == live));
        Assert.Equal(2, await db.MemberChatTurns.CountAsync(t => t.SessionId == live));
        Assert.Equal(2, await db.MemberChatTurnUsages.CountAsync(
            u => db.MemberChatTurns.Any(t => t.Id == u.TurnId && t.SessionId == live)));
    }

    /// <summary>Nothing to delete is a no-op, not an empty transaction or a throw.</summary>
    [Fact]
    public async Task DeletingNoConversations_RemovesNothing()
    {
        var report = await DeleteSessionsAsync([]);

        Assert.Equal(0, report.Sessions);
        Assert.Equal(0, report.Turns);
        Assert.Equal(0, report.Usages);
    }

    // ---- Harness -----------------------------------------------------------------------------

    private async Task<IReadOnlyList<Guid>> FindDueAsync(int limit)
    {
        using var scope = _services.CreateScope();
        var repository = new UserRepository(
            scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>());
        return await repository.GetAccountsDueForErasureAsync(
            DateTime.UtcNow - UserService.DeletionGracePeriod, limit);
    }

    private async Task<IReadOnlyList<Guid>> FindExpiredSessionsAsync(int cutoffDaysAgo, int limit)
    {
        using var scope = _services.CreateScope();
        var sut = new ChatRetentionService(
            scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>());
        return await sut.FindExpiredSessionsAsync(DateTime.UtcNow.AddDays(-cutoffDaysAgo), limit);
    }

    private async Task<ChatRetentionReport> DeleteSessionsAsync(IReadOnlyList<Guid> sessionIds)
    {
        using var scope = _services.CreateScope();
        var sut = new ChatRetentionService(
            scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>());
        return await sut.DeleteSessionsAsync(sessionIds);
    }

    private async Task<Guid> SeedOrganizationAsync()
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();

        var organization = new Organization { Name = "Doe family", Type = OrganizationType.Family };
        db.Organizations.Add(organization);
        await db.SaveChangesAsync();
        return organization.Id;
    }

    private async Task<Guid> SeedUserAsync(Guid organizationId, int? requestedDaysAgo)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();

        var user = new User
        {
            OrganizationId = organizationId,
            Email = $"caregiver-{Guid.NewGuid():N}@example.com",
            Name = "Jane Doe",
            DeletionRequestedAtUtc = requestedDaysAgo is { } days
                ? DateTime.UtcNow.AddDays(-days)
                : null,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private async Task<(Guid OrganizationId, Guid UserId, Guid MemberId)> SeedMemberAsync()
    {
        var organizationId = await SeedOrganizationAsync();
        var userId = await SeedUserAsync(organizationId, requestedDaysAgo: null);

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();

        var member = new CardiMember
        {
            OrganizationId = organizationId,
            Name = "Margaret Doe",
            DateOfBirth = new DateOnly(1948, 4, 2),
            Gender = Gender.Female,
            IsActive = true,
        };
        db.CardiMembers.Add(member);
        await db.SaveChangesAsync();

        return (organizationId, userId, member.Id);
    }

    /// <summary>
    /// One conversation, with a turn (and a usage row) for each age given. Content is synthesised
    /// — this repository is public, so no test carries a real caregiver's question.
    /// </summary>
    private async Task<Guid> SeedSessionAsync(
        Guid userId, Guid memberId, int startedDaysAgo, int[] turnDaysAgo)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();

        var startedAt = DateTime.UtcNow.AddDays(-startedDaysAgo);
        var session = new MemberChatSession
        {
            CardiMemberId = memberId,
            UserId = userId,
            StartedAtUtc = startedAt,
            LastTurnAtUtc = turnDaysAgo.Length == 0
                ? startedAt
                : DateTime.UtcNow.AddDays(-turnDaysAgo.Min()),
        };
        db.MemberChatSessions.Add(session);
        await db.SaveChangesAsync();

        foreach (var daysAgo in turnDaysAgo)
        {
            var turn = new MemberChatTurn
            {
                SessionId = session.Id,
                Role = ChatTurnRole.User,
                Content = "encrypted-question",
                CreatedAtUtc = DateTime.UtcNow.AddDays(-daysAgo),
            };
            db.MemberChatTurns.Add(turn);
            await db.SaveChangesAsync();

            db.MemberChatTurnUsages.Add(new MemberChatTurnUsage
            {
                TurnId = turn.Id,
                Step = AiCallStep.Rewrite,
                ProviderSlot = AiProviderSlot.Rewrite,
                ModelName = "test-model",
            });
        }

        await db.SaveChangesAsync();
        return session.Id;
    }
}
