using CardiTrack.Application.Interfaces.Clients;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Persistence;
using CardiTrack.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Testcontainers.PostgreSql;

namespace CardiTrack.IntegrationTests.Services;

/// <summary>
/// Whether erasure actually erases. The claim this makes — that nothing referencing the member
/// survives — is a claim about foreign keys, cascade order and twenty-six tables, so it is worth
/// nothing against a substitute: it runs against a real Postgres and reads every table back.
/// </summary>
/// <remarks>
/// The failure this guards against is the one #148 describes: deleting a CardiMember today leaves
/// <c>ActivityLogs</c>, <c>Alerts</c> and <c>PatternBaselines</c> live and queryable. A test that
/// only checked the member row was gone would have passed against exactly that.
/// </remarks>
public class MemberErasureCascadeTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithCleanUp(true)
        .Build();

    private ServiceProvider _services = null!;
    private readonly IProfilePhotoStorage _photos = Substitute.For<IProfilePhotoStorage>();
    private readonly IReportStorage _reportStorage = Substitute.For<IReportStorage>();

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

    /// <summary>
    /// The whole point: a member with rows in every table the runbook names is erased, and every
    /// one of those tables comes back empty for them.
    /// </summary>
    [Fact]
    public async Task ErasingAMember_LeavesNothingBehindInAnyTable()
    {
        var (organizationId, userId, memberId) = await SeedMemberWithDataAsync();

        var report = await EraseAsync(memberId);

        Assert.True(report.TotalRows > 0, "the seed wrote rows, so the erasure must have removed some");

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();

        // Named one at a time rather than swept in a loop: a loop over DbSets would silently start
        // passing for a table nobody remembered to seed, and the failure this guards against is
        // exactly a table nobody remembered.
        Assert.Equal(0, await db.CardiMembers.CountAsync(x => x.Id == memberId));
        Assert.Equal(0, await db.UserCardiMembers.CountAsync(x => x.CardiMemberId == memberId));
        Assert.Equal(0, await db.ActivityLogs.CountAsync(x => x.CardiMemberId == memberId));
        Assert.Equal(0, await db.DeviceActivityLogs.CountAsync(x => x.CardiMemberId == memberId));
        Assert.Equal(0, await db.Alerts.CountAsync(x => x.CardiMemberId == memberId));
        Assert.Equal(0, await db.PatternBaselines.CountAsync(x => x.CardiMemberId == memberId));
        Assert.Equal(0, await db.AlertPreferences.CountAsync(x => x.CardiMemberId == memberId));
        Assert.Equal(0, await db.MemberChatSessions.CountAsync(x => x.CardiMemberId == memberId));
        Assert.Equal(0, await db.MemberStatusLines.CountAsync(x => x.CardiMemberId == memberId));
        Assert.Equal(0, await db.MetricAlarms.CountAsync(x => x.CardiMemberId == memberId));
        Assert.Equal(0, await db.DeviceConnections.CountAsync(x => x.CardiMemberId == memberId));
        Assert.Equal(0, await db.CardiMemberCreationKeys.CountAsync(x => x.CardiMemberId == memberId));
        Assert.Equal(0, await db.Notifications.CountAsync(x => x.CardiMemberId == memberId));

        // The caregiver and their organization are not the member's to take with them.
        Assert.Equal(1, await db.Users.CountAsync(x => x.Id == userId));
        Assert.Equal(1, await db.Organizations.CountAsync(x => x.Id == organizationId));
    }

    /// <summary>
    /// The member's chat turns are what carry the questions and answers. They cascade from the
    /// session by configuration, which is precisely why they are worth asserting: a configuration
    /// change would take the cascade with it and nothing else would notice.
    /// </summary>
    [Fact]
    public async Task ErasingAMember_TakesTheChatTurnsWithTheSession()
    {
        var (_, _, memberId) = await SeedMemberWithDataAsync();

        Guid sessionId;
        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
            sessionId = await db.MemberChatSessions.Where(s => s.CardiMemberId == memberId)
                .Select(s => s.Id).SingleAsync();
            Assert.True(await db.MemberChatTurns.AnyAsync(t => t.SessionId == sessionId));
        }

        await EraseAsync(memberId);

        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
            Assert.Equal(0, await db.MemberChatTurns.CountAsync(t => t.SessionId == sessionId));
        }
    }

    /// <summary>
    /// The report is durable export metadata naming the member in an array column, and the export
    /// file itself lives in GCS. Both go — the row is not the data.
    /// </summary>
    [Fact]
    public async Task ErasingAMember_RemovesReportsNamingThem_AndTheirExportObjects()
    {
        var (_, userId, memberId) = await SeedMemberWithDataAsync();

        var report = await EraseAsync(memberId);

        await _reportStorage.Received(1).DeleteAsync("reports/seeded-export.pdf", Arg.Any<CancellationToken>());
        Assert.Empty(report.OrphanedObjects);

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
        Assert.Equal(0, await db.Reports.CountAsync(r => r.OwnerUserId == userId));
    }

    /// <summary>
    /// A storage object that will not delete must not fail the erasure — the rows are already
    /// committed by then, so throwing would report a completed erasure as a failed one. It is
    /// named in the report instead, which is what a manual clean-up needs.
    /// </summary>
    [Fact]
    public async Task AnUndeletableObject_IsReportedRatherThanThrown()
    {
        var (_, _, memberId) = await SeedMemberWithDataAsync();
        _reportStorage
            .DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw new InvalidOperationException("bucket unreachable"));

        var report = await EraseAsync(memberId);

        Assert.Contains("reports/seeded-export.pdf", report.OrphanedObjects);

        // The rows still went. A file left behind is recoverable; rows left behind are the breach.
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
        Assert.Equal(0, await db.CardiMembers.CountAsync(x => x.Id == memberId));
    }

    /// <summary>
    /// Erasing one member leaves the other alone. Obvious, and worth a test: every delete in the
    /// cascade is a set-based statement, and a missing predicate would empty the table.
    /// </summary>
    [Fact]
    public async Task ErasingOneMember_LeavesAnotherUntouched()
    {
        var (organizationId, userId, first) = await SeedMemberWithDataAsync();
        var second = await SeedSecondMemberAsync(organizationId, userId);

        await EraseAsync(first);

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
        Assert.Equal(1, await db.CardiMembers.CountAsync(x => x.Id == second));
        Assert.True(await db.ActivityLogs.AnyAsync(x => x.CardiMemberId == second));
        Assert.Equal(1, await db.UserCardiMembers.CountAsync(x => x.CardiMemberId == second));
    }

    /// <summary>The report is the evidence the runbook asks an operator to record by hand.</summary>
    [Fact]
    public async Task TheReport_NamesEveryTableItTouched_InCascadeOrder()
    {
        var (_, _, memberId) = await SeedMemberWithDataAsync();

        var report = await EraseAsync(memberId);

        var tables = report.RowsByTable.Select(r => r.Table).ToList();
        Assert.Equal(memberId, report.CardiMemberId);

        // Children before parents: if CardiMembers were not last, the deletes before it would be
        // running against rows whose foreign key had already gone.
        Assert.Equal("CardiMembers", tables[^1]);
        Assert.True(tables.IndexOf("ActivityLogs") < tables.IndexOf("CardiMembers"));
        Assert.True(tables.IndexOf("MemberChatTurns") < tables.IndexOf("MemberChatSessions"));
        Assert.Contains("DeviceActivityLogs", tables);
    }

    private async Task<MemberErasureReport> EraseAsync(Guid memberId)
    {
        using var scope = _services.CreateScope();
        var sut = new MemberErasureService(
            scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>(),
            _photos,
            _reportStorage,
            NullLogger<MemberErasureService>.Instance);
        return await sut.EraseAsync(memberId);
    }

    /// <summary>
    /// A member with something in every table a caregiver's use of the app would fill. Synthesised
    /// figures throughout — this repository is public, so no test carries a real wearer's reading.
    /// </summary>
    private async Task<(Guid OrganizationId, Guid UserId, Guid MemberId)> SeedMemberWithDataAsync()
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();

        var organization = new Organization { Name = "Doe family", Type = OrganizationType.Family };
        db.Organizations.Add(organization);

        var user = new User
        {
            OrganizationId = organization.Id,
            Email = $"caregiver-{Guid.NewGuid():N}@example.com",
            Name = "Jane Doe",
        };
        db.Users.Add(user);

        var member = new CardiMember
        {
            OrganizationId = organization.Id,
            Name = "Margaret Doe",
            DateOfBirth = new DateOnly(1948, 4, 2),
            Gender = Gender.Female,
            IsActive = true,
        };
        db.CardiMembers.Add(member);
        await db.SaveChangesAsync();

        db.UserCardiMembers.Add(new UserCardiMember
        {
            UserId = user.Id,
            CardiMemberId = member.Id,
            RelationshipType = RelationshipType.Parent,
            IsPrimaryCaregiver = true,
            CanViewHealthData = true,
            ReceiveAlerts = true,
        });

        db.ActivityLogs.Add(new ActivityLog
        {
            CardiMemberId = member.Id,
            Date = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)),
            Steps = 4200,
        });
        db.DeviceActivityLogs.Add(new DeviceActivityLog
        {
            CardiMemberId = member.Id,
            Date = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)),
            Steps = 4200,
        });
        db.PatternBaselines.Add(new PatternBaseline
        {
            CardiMemberId = member.Id,
            CalculatedDate = DateTime.UtcNow,
            AvgSteps = 4100,
        });
        db.Alerts.Add(new Alert
        {
            CardiMemberId = member.Id,
            Title = "Quieter than usual",
            Message = "Fewer steps than their usual pattern.",
        });
        db.AlertPreferences.Add(new AlertPreference { CardiMemberId = member.Id });
        db.Notifications.Add(new Notification
        {
            OrganizationId = organization.Id,
            UserId = user.Id,
            CardiMemberId = member.Id,
            RuleCode = "steps.quiet",
            TitleKey = "notification.steps.quiet.title",
            BodyKey = "notification.steps.quiet.body",
            Fingerprint = Guid.NewGuid().ToString("N"),
        });
        db.MemberStatusLines.Add(new MemberStatusLine
        {
            CardiMemberId = member.Id,
            Message = "Nothing unusual today.",
            GeneratedAtUtc = DateTime.UtcNow,
        });
        db.MetricAlarms.Add(new MetricAlarm
        {
            CardiMemberId = member.Id,
            OrganizationId = organization.Id,
            Name = "Quiet day",
        });
        db.DeviceConnections.Add(new DeviceConnection
        {
            CardiMemberId = member.Id,
            DeviceType = DeviceType.Fitbit,
        });
        db.CardiMemberCreationKeys.Add(new CardiMemberCreationKey
        {
            UserId = user.Id,
            Key = Guid.NewGuid().ToString("N"),
            CardiMemberId = member.Id,
        });
        db.Reports.Add(new Report
        {
            OwnerUserId = user.Id,
            CardiMemberIds = [member.Id],
            ObjectName = "reports/seeded-export.pdf",
        });

        var session = new MemberChatSession
        {
            UserId = user.Id,
            CardiMemberId = member.Id,
            StartedAtUtc = DateTime.UtcNow,
            LastTurnAtUtc = DateTime.UtcNow,
        };
        db.MemberChatSessions.Add(session);
        await db.SaveChangesAsync();

        db.MemberChatTurns.Add(new MemberChatTurn
        {
            SessionId = session.Id,
            Role = ChatTurnRole.User,
            Content = "How has she been this week?",
        });
        await db.SaveChangesAsync();

        return (organization.Id, user.Id, member.Id);
    }

    private async Task<Guid> SeedSecondMemberAsync(Guid organizationId, Guid userId)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();

        var member = new CardiMember
        {
            OrganizationId = organizationId,
            Name = "Arthur Doe",
            DateOfBirth = new DateOnly(1946, 9, 9),
            Gender = Gender.Male,
            IsActive = true,
        };
        db.CardiMembers.Add(member);
        await db.SaveChangesAsync();

        db.UserCardiMembers.Add(new UserCardiMember
        {
            UserId = userId,
            CardiMemberId = member.Id,
            RelationshipType = RelationshipType.Parent,
            CanViewHealthData = true,
        });
        db.ActivityLogs.Add(new ActivityLog
        {
            CardiMemberId = member.Id,
            Date = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)),
            Steps = 3100,
        });
        await db.SaveChangesAsync();

        return member.Id;
    }
}
