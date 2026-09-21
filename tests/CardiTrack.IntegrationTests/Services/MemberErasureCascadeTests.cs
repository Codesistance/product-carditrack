using CardiTrack.Application.Interfaces.Clients;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Persistence;
using CardiTrack.Infrastructure.Services;
using Microsoft.Extensions.Logging;
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
    private readonly IOAuthGrantRevoker _grantRevoker = Substitute.For<IOAuthGrantRevoker>();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        var sc = new ServiceCollection();
        sc.AddDbContext<CardiTrackDbContext>(options =>
            options.UseNpgsql(_container.GetConnectionString(),
                b => b.MigrationsAssembly("CardiTrack.Infrastructure")));
        sc.AddScoped<ITimeSeriesPartitionService, TimeSeriesPartitionService>();
        sc.AddLogging();
        _services = sc.BuildServiceProvider();

        using var scope = _services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>().Database.MigrateAsync();

        // Four of the tables in the cascade are partitioned by date, and a freshly migrated
        // database has no partition for today — `PartitionMaintenanceWorker` creates them in a
        // running system. Without this the seed cannot write a digest, an assessment, a granular
        // hour or a rollup, and the erasure would be tested against the tables that are easy.
        await scope.ServiceProvider.GetRequiredService<ITimeSeriesPartitionService>()
            .EnsureUpcomingPartitionsAsync(daysAhead: 7);
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

        // Every table in the cascade, named one at a time. A loop over DbSets would silently
        // start passing for a table nobody remembered to seed, and a table nobody remembered is
        // precisely the failure this exists to catch.
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
        Assert.Equal(0, await db.MetricAlarmStates.CountAsync(x => x.CardiMemberId == memberId));
        Assert.Equal(0, await db.DeviceConnections.CountAsync(x => x.CardiMemberId == memberId));
        Assert.Equal(0, await db.CardiMemberCreationKeys.CountAsync(x => x.CardiMemberId == memberId));
        Assert.Equal(0, await db.Notifications.CountAsync(x => x.CardiMemberId == memberId));
        Assert.Equal(0, await db.NotificationDeliveries.CountAsync(x => x.CardiMemberId == memberId));
        Assert.Equal(0, await db.NotificationMutes.CountAsync(x => x.CardiMemberId == memberId));
        Assert.Equal(0, await db.RealtimeAssessments.CountAsync(x => x.CardiMemberId == memberId));
        Assert.Equal(0, await db.DigestEntries.CountAsync(x => x.CardiMemberId == memberId));
        Assert.Equal(0, await db.EnvironmentalReadings.CountAsync(x => x.CardiMemberId == memberId));
        Assert.Equal(0, await db.GranularMetricHours.CountAsync(x => x.CardiMemberId == memberId));
        Assert.Equal(0, await db.MetricRollupsHourly.CountAsync(x => x.CardiMemberId == memberId));
        Assert.Equal(0, await db.MemberQuestionnaires.CountAsync(x => x.CardiMemberId == memberId));
        Assert.Equal(0, await db.Set<MemberAdvise>().CountAsync(x => x.CardiMemberId == memberId));
        Assert.Equal(0, await db.MemberAdviseObservations.CountAsync(x => x.CardiMemberId == memberId));
        Assert.Equal(0, await db.Set<MemberInsight>().CountAsync(x => x.CardiMemberId == memberId));
        Assert.Equal(0, await db.MemberAiHolds.CountAsync(x => x.CardiMemberId == memberId));
        Assert.Equal(0, await db.GenerationLeases.CountAsync(x => x.CardiMemberId == memberId));
        Assert.Equal(0, await db.DeviceHistoryRepulls.CountAsync(x => x.CardiMemberId == memberId));
        Assert.Equal(0, await db.ExportConsents.CountAsync(x => x.CardiMemberIds.Contains(memberId)));
        Assert.Equal(0, await db.Reports.CountAsync(x => x.CardiMemberIds.Contains(memberId)));

        // Emptiness proves nothing if the seed never wrote there. Every step that names a table
        // this test seeded must report having removed something, or the assertions above are
        // passing on absence rather than on deletion.
        var seeded = report.RowsByTable
            .Where(r => r.Table is not ("MemberChatTurnUsages" or "MemberAdvises"))
            .ToList();
        Assert.All(seeded, r => Assert.True(
            r.Rows > 0, $"{r.Table} was seeded but the cascade removed nothing from it"));

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
        _photos.DeleteAllForMemberAsync(memberId, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<string>());

        var report = await EraseAsync(memberId);

        await _reportStorage.Received(1).DeleteAsync("reports/seeded-export.pdf", Arg.Any<CancellationToken>());

        // The member's whole photo prefix, not the one object the row named — an interrupted
        // replacement leaves a second face photo that nothing points at.
        await _photos.Received(1).DeleteAllForMemberAsync(memberId, Arg.Any<CancellationToken>());
        Assert.True(report.PhotoObjectRemoved);
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

        Assert.Equal(memberId, report.CardiMemberId);

        // The runbook's member-scoped order, in full. Spot checks let a middle step be removed or
        // renamed without a failure, and the order is the one thing about a cascade that cannot be
        // inferred from anywhere else — so it is written out and compared whole.
        string[] expected =
        [
            "NotificationDeliveries",
            "NotificationMutes",
            "Notifications",
            "AlertPreferences",
            "Alerts",
            "PatternBaselines",
            "RealtimeAssessments",
            "DigestEntries",
            "EnvironmentalReadings",
            "GranularMetricHours",
            "MetricRollupsHourly",
            "DeviceActivityLogs",
            "ActivityLogs",
            "MemberQuestionnaires",
            "MemberChatTurnUsages",
            "MemberChatTurns",
            "MemberChatSessions",
            "MemberAdvises",
            "MemberAdviseObservations",
            "MemberInsights",
            "MetricAlarmStates",
            "MetricAlarms",
            "MemberStatusLines",
            "MemberAiHolds",
            "GenerationLeases",
            "DeviceHistoryRepulls",
            "ExportConsents",
            "Reports",
            "DeviceConnections",
            "CardiMemberCreationKeys",
            "UserCardiMembers",
            "CardiMembers",
        ];

        Assert.Equal(expected, report.RowsByTable.Select(r => r.Table));
    }

    /// <summary>
    /// Same rule as the account cascade: once the rows are committed, the names of the storage
    /// objects they pointed at exist nowhere else, so the clean-up runs to completion rather than
    /// honouring a shutdown. A cancellation that escaped here would turn a reportable orphan — a
    /// complete identified health export, or a member's face — into an unfindable one.
    /// </summary>
    [Fact]
    public async Task ACancellationWhileClearingStorage_IsReportedAsOrphaned_NotThrown()
    {
        var (_, _, memberId) = await SeedMemberWithDataAsync();
        _photos.DeleteAllForMemberAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<IReadOnlyList<string>>(new OperationCanceledException()));
        _reportStorage.DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException(new OperationCanceledException()));

        var report = await EraseAsync(memberId);

        Assert.Contains($"members/{memberId}/", report.OrphanedObjects);
        Assert.Contains("reports/seeded-export.pdf", report.OrphanedObjects);

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
        Assert.Equal(0, await db.CardiMembers.CountAsync(m => m.Id == memberId));
    }

    /// <summary>
    /// And the delete really is given <see cref="CancellationToken.None"/> — the assertion above
    /// would pass on a swallowed cancellation from the caller's token too.
    /// </summary>
    [Fact]
    public async Task TheStorageDeletesAfterTheCommit_AreNotGivenTheCallersToken()
    {
        var (_, _, memberId) = await SeedMemberWithDataAsync();

        // A live, distinct token — not the default. Called with CancellationToken.None these
        // assertions prove nothing, because forwarding `ct` would hand the storage the same value.
        using var caller = new CancellationTokenSource();

        await EraseAsync(memberId, caller.Token);

        await _photos.Received(1).DeleteAllForMemberAsync(
            memberId, Arg.Is<CancellationToken>(t => t == CancellationToken.None));
        await _reportStorage.Received(1).DeleteAsync(
            "reports/seeded-export.pdf", Arg.Is<CancellationToken>(t => t == CancellationToken.None));
    }

    /// <summary>
    /// The grant is ended before the row holding its token is deleted — the runbook's "revoke
    /// upstream before deleting" step, which until now an operator had to remember.
    /// </summary>
    [Fact]
    public async Task ErasingAMember_RevokesItsDeviceGrantsFirst()
    {
        var (_, _, memberId) = await SeedMemberWithDataAsync();

        await EraseAsync(memberId);

        await _grantRevoker.Received(1).TryRevokeAsync(
            Arg.Is<DeviceConnection>(c => c.CardiMemberId == memberId), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A grant that could not be revoked is named in the report, not swallowed. This is the one
    /// failure in the cascade that can never be retried: the refresh token goes with the row, so
    /// nothing will ever be able to end that grant again, and the only route left is the wearer's
    /// own provider account. An erasure that reported success here would be claiming something
    /// untrue about a live grant on a person's health data.
    /// </summary>
    [Fact]
    public async Task AGrantThatCouldNotBeRevoked_IsNamedInTheReport()
    {
        var (_, _, memberId) = await SeedMemberWithDataAsync();
        _grantRevoker.TryRevokeAsync(Arg.Any<DeviceConnection>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var report = await EraseAsync(memberId);

        var connectionId = Assert.Single(report.UnrevokedGrants);

        // And the erasure still finished — the report is the only record that anything is left.
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
        Assert.Equal(0, await db.DeviceConnections.CountAsync(c => c.Id == connectionId));
        Assert.Equal(0, await db.CardiMembers.CountAsync(m => m.Id == memberId));
    }

    private async Task<MemberErasureReport> EraseAsync(
        Guid memberId, CancellationToken ct = default)
    {
        using var scope = _services.CreateScope();
        var sut = new MemberErasureService(
            scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>(),
            _photos,
            _reportStorage,
            _grantRevoker,
            NullLogger<MemberErasureService>.Instance);
        return await sut.EraseAsync(memberId, ct);
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
            PhotoObjectName = $"members/{Guid.NewGuid():N}/photo.jpg",
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
        db.MemberAdviseObservations.Add(new MemberAdviseObservation
        {
            CardiMemberId = member.Id,
            Topic = AdviseTopic.Activity,
            Summary = "Steps have been below her usual this week.",
            Suggestion = "A short walk after lunch is worth trying.",
            GuidelineCited = "WHO adult activity guidance",
            ObservedAtUtc = DateTime.UtcNow.AddDays(-3),
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
        db.ExportConsents.Add(new ExportConsent
        {
            OwnerUserId = user.Id,
            CardiMemberIds = [member.Id],
            DateRangeFrom = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-7)),
            DateRangeTo = DateOnly.FromDateTime(DateTime.UtcNow),
            PolicyVersion = "2026-09-01",
        });
        db.NotificationDeliveries.Add(new NotificationDelivery
        {
            SourceId = Guid.NewGuid(),
            UserId = user.Id,
            CardiMemberId = member.Id,
        });
        db.NotificationMutes.Add(new NotificationMute
        {
            UserId = user.Id,
            CardiMemberId = member.Id,
            MutedDate = DateTime.UtcNow,
        });
        db.RealtimeAssessments.Add(new RealtimeAssessment
        {
            CardiMemberId = member.Id,
            WindowStartUtc = DateTime.UtcNow.AddHours(-1),
            WindowEndUtc = DateTime.UtcNow,
        });
        db.DigestEntries.Add(new DigestEntry
        {
            CardiMemberId = member.Id,
            LocalDate = DateOnly.FromDateTime(DateTime.UtcNow),
            Text = "A steady day.",
        });
        db.MemberQuestionnaires.Add(new MemberQuestionnaire
        {
            CardiMemberId = member.Id,
            QuestionText = "Has she been sleeping well?",
        });
        db.Set<MemberAdvise>().Add(new MemberAdvise
        {
            CardiMemberId = member.Id,
            Summary = "A short walk after lunch.",
        });
        // Model-written prose about this member, at one of the three scopes. Seeded because the
        // assertion below proves deletion rather than emptiness, and an unseeded table would let
        // the cascade pass by never having had anything to remove.
        db.Set<MemberInsight>().Add(new MemberInsight
        {
            CardiMemberId = member.Id,
            Scope = InsightScope.Baseline,
            Summary = "Steadier than last week.",
            GeneratedAtUtc = DateTime.UtcNow,
        });
        db.MemberAiHolds.Add(new MemberAiHold
        {
            CardiMemberId = member.Id,
            HeldUntilUtc = DateTime.UtcNow.AddHours(1),
            LastFailedAtUtc = DateTime.UtcNow,
        });
        db.GenerationLeases.Add(new GenerationLease
        {
            CardiMemberId = member.Id,
            Work = GenerationWork.Weekbook,
            PeriodEnd = new DateOnly(2026, 9, 6),
            HeldUntilUtc = DateTime.UtcNow.AddMinutes(20),
            ClaimedAtUtc = DateTime.UtcNow,
        });

        // These carry DeviceConnectionId, so they wait until the connection above has an id.
        await db.SaveChangesAsync();
        var connectionId = await db.DeviceConnections
            .Where(c => c.CardiMemberId == member.Id).Select(c => c.Id).FirstAsync();
        var alarmId = await db.MetricAlarms
            .Where(a => a.CardiMemberId == member.Id).Select(a => a.Id).FirstAsync();

        db.GranularMetricHours.Add(new GranularMetricHour
        {
            DeviceConnectionId = connectionId,
            CardiMemberId = member.Id,
            Metric = GranularMetric.HeartRate,
            HourStartUtc = DateTime.UtcNow.AddHours(-2),
        });
        db.MetricRollupsHourly.Add(new MetricRollupHourly
        {
            CardiMemberId = member.Id,
            Metric = GranularMetric.HeartRate,
            HourStartUtc = DateTime.UtcNow.AddHours(-2),
            Min = 58,
            Max = 74,
        });
        db.EnvironmentalReadings.Add(new EnvironmentalReading
        {
            CardiMemberId = member.Id,
            DeviceConnectionId = connectionId,
            SessionStartUtc = DateTime.UtcNow.AddHours(-2),
            SessionEndUtc = DateTime.UtcNow.AddHours(-1),
        });
        db.DeviceHistoryRepulls.Add(new DeviceHistoryRepull
        {
            DeviceConnectionId = connectionId,
            CardiMemberId = member.Id,
            RequestedByUserId = user.Id,
            FromDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-30)),
            ToDate = DateOnly.FromDateTime(DateTime.UtcNow),
        });
        db.MetricAlarmStates.Add(new MetricAlarmState
        {
            MetricAlarmId = alarmId,
            CardiMemberId = member.Id,
            StateSinceUtc = DateTime.UtcNow.AddHours(-3),
            LastEvaluatedUtc = DateTime.UtcNow,
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
