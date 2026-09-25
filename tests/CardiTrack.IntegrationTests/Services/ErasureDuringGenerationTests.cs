using System.Data.Common;
using CardiTrack.Application.Interfaces.Clients;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Persistence;
using CardiTrack.Infrastructure.Repositories;
using CardiTrack.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NSubstitute;
using Testcontainers.PostgreSql;

namespace CardiTrack.IntegrationTests.Services;

/// <summary>
/// Whether a generation that was already running when its member was erased can put health data
/// back. Every AI writer in the product reads the member, spends seconds to minutes in a model
/// call, then writes a row naming that <c>CardiMemberId</c> — and there is no foreign key to stop
/// the write landing after the erasure has swept the table it goes in.
/// </summary>
/// <remarks>
/// <para>
/// Against a real Postgres, and it has to be: the claim is about two concurrent transactions, row
/// locks and what one of them can see of the other's uncommitted deletes. Every substitute answers
/// yes to all of it. The same reasoning <see cref="MemberErasureCascadeTests"/> gives for testing
/// the cascade against a container applies here twice over.
/// </para>
/// <para>
/// The model call is not mocked out of the picture, it is <em>suspended</em>: each case holds a
/// <see cref="TaskCompletionSource"/> open where the inference would be, runs the erasure to
/// completion while it is held, and only then lets the write proceed. That is the real interleaving
/// — not a write to an already-erased member, which any check would catch, but a write whose
/// decision to proceed was made while the member still existed.
/// </para>
/// <para>
/// Written against <see cref="IMemberWriteGuard"/> and the two repositories that hold it, rather
/// than by driving all nine generators through substituted models. That is where the guarantee
/// lives: every generator reaches its table through one of these, so a case per table is a case per
/// generator, and the assertions can name the table the way the cascade test does rather than
/// assert on a generator's return value.
/// </para>
/// </remarks>
public class ErasureDuringGenerationTests : IAsyncLifetime
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
        sc.AddScoped<IMemberWriteGuard, MemberWriteGuard>();
        sc.AddScoped<IFamilyWriteGuard, FamilyWriteGuard>();
        sc.AddLogging();
        _services = sc.BuildServiceProvider();

        using var scope = _services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>().Database.MigrateAsync();

        // DigestEntries and RealtimeAssessments are partitioned by date and a freshly migrated
        // database has no partition for today — the same seed step MemberErasureCascadeTests needs,
        // and for the same reason: without it the write under test cannot land at all, and a test
        // that cannot write proves nothing about a write being refused.
        await scope.ServiceProvider.GetRequiredService<ITimeSeriesPartitionService>()
            .EnsureUpcomingPartitionsAsync(daysAhead: 7);

        _photos.DeleteAllForMemberAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<string>());
        _grantRevoker.TryRevokeAsync(Arg.Any<DeviceConnection>(), Arg.Any<CancellationToken>())
            .Returns(true);
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

    // ── The digest table: Daybook, Weekbook, Monthbook and the family series ────────────────

    [Fact]
    public async Task ADigestGeneratedAcrossAnErasure_IsNotStored()
    {
        var memberId = await SeedMemberAsync();

        var stored = await RaceAgainstErasureAsync(memberId, async (scope, ct) =>
        {
            var digests = new DigestRepository(
                scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>(),
                scope.ServiceProvider.GetRequiredService<IMemberWriteGuard>());

            return await digests.AddAsync(new DigestEntry
            {
                CardiMemberId = memberId,
                LocalDate = DateOnly.FromDateTime(DateTime.UtcNow),
                Audience = DigestAudience.Daybook,
                Headline = "A steady day",
                Text = "Nothing stood out today.",
                GeneratedAtUtc = DateTime.UtcNow,
                PromptVersion = 1,
            }, ct);
        });

        Assert.False(stored, "the digest was composed for a member who no longer exists");
        await AssertNothingGeneratedSurvivesAsync(memberId);
    }

    // ── The assessment table, and the alert its claim would have routed ─────────────────────

    [Fact]
    public async Task AnAssessmentGeneratedAcrossAnErasure_IsNotStoredAndClaimsNothing()
    {
        var memberId = await SeedMemberAsync();
        var windowEnd = DateTime.UtcNow;

        var claimed = await RaceAgainstErasureAsync(memberId, async (scope, ct) =>
        {
            var assessments = new RealtimeAssessmentRepository(
                scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>(),
                scope.ServiceProvider.GetRequiredService<IMemberWriteGuard>());

            return await assessments.UpsertAsync(new RealtimeAssessment
            {
                CardiMemberId = memberId,
                WindowStartUtc = windowEnd.AddMinutes(-30),
                WindowEndUtc = windowEnd,
                HrTrendLast = 96,
                HrDeviationScore = 3.1,
                HrNoiseRms = 1.2,
                ModelOutput = "Heart rate is running high.",
                RawSeverity = "orange",
                Severity = AlertSeverity.Orange,
                SsaEngine = "test",
                GeneratedAtUtc = windowEnd,
            }, ct);
        });

        // Not merely "did not write" — did not *claim*. The caller routes an alert only to the
        // pass that inserted, so a refusal reading as a claim would page a family about a member
        // the product has just forgotten.
        Assert.False(claimed, "a refused assessment must not read as an exclusive claim on the window");
        await AssertNothingGeneratedSurvivesAsync(memberId);
    }

    // ── The change-tracked tables: insights, advise, status lines, alerts, chat turns ───────

    [Fact]
    public async Task AChangeTrackedGenerationAcrossAnErasure_IsNotStored()
    {
        var memberId = await SeedMemberAsync();

        var stored = await RaceAgainstErasureAsync(memberId, async (scope, ct) =>
        {
            var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
            var guard = scope.ServiceProvider.GetRequiredService<IMemberWriteGuard>();

            // One guarded save over every table the EF-tracked writers use, which is the shape
            // each of them has: stage first, then save through the guard.
            db.Set<MemberInsight>().Add(new MemberInsight
            {
                CardiMemberId = memberId,
                Scope = InsightScope.Trend,
                Summary = "Steps have been drifting down.",
                GeneratedAtUtc = DateTime.UtcNow,
                PromptVersion = 1,
            });
            db.Set<MemberAdvise>().Add(new MemberAdvise
            {
                CardiMemberId = memberId,
                Topic = AdviseTopic.Sleep,
                Summary = "Sleep has been short.",
                Suggestion = "An earlier bedtime may help.",
                GeneratedAtUtc = DateTime.UtcNow,
                PromptVersion = 1,
            });
            db.MemberStatusLines.Add(new MemberStatusLine
            {
                CardiMemberId = memberId,
                Message = "Doing well today.",
                GeneratedAtUtc = DateTime.UtcNow,
            });
            // The question the digest decides to ask the family — written after the same model
            // call the digest itself comes from, so it races exactly as the digest does.
            db.MemberQuestionnaires.Add(new MemberQuestionnaire
            {
                CardiMemberId = memberId,
                QuestionText = "How has she been sleeping?",
                Status = QuestionnaireStatus.Pending,
                GeneratedAtUtc = DateTime.UtcNow,
                Scope = QuestionnaireScope.Permanent,
            });
            db.Alerts.Add(new Alert
            {
                CardiMemberId = memberId,
                AlertType = AlertType.HeartRate,
                Severity = AlertSeverity.Orange,
                Title = "Heart rate worth checking on",
                Message = "Running high for the last half hour.",
                TriggeredDate = DateTime.UtcNow,
            });

            return await guard.WriteIfMemberLivesAsync(memberId, _ => db.SaveChangesAsync(ct), ct);
        });

        Assert.False(stored, "every staged row described a member who no longer exists");
        await AssertNothingGeneratedSurvivesAsync(memberId);
    }

    /// <summary>
    /// The hold the digest writes when the model does not finish. A failure path, but it still
    /// writes a member-scoped row minutes after the member was read.
    /// </summary>
    [Fact]
    public async Task AnAiHoldWrittenAcrossAnErasure_IsNotStored()
    {
        var memberId = await SeedMemberAsync();

        await RaceAgainstErasureAsync(memberId, async (scope, ct) =>
        {
            var holds = new MemberAiHoldRepository(
                scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>(),
                scope.ServiceProvider.GetRequiredService<IMemberWriteGuard>());

            await holds.UpsertAsync(new MemberAiHold
            {
                CardiMemberId = memberId,
                Purpose = AiHoldPurpose.FamilyDigest,
                HeldUntilUtc = DateTime.UtcNow.AddHours(1),
                LastFailedAtUtc = DateTime.UtcNow,
                ConsecutiveFailures = 1,
                Reason = "truncated",
            }, ct);
            return false;
        });

        await AssertNothingGeneratedSurvivesAsync(memberId);
    }

    // ── The other ordering ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// The write wins the race. Erasure must then wait for it rather than skipping past, and sweep
    /// the row it wrote — otherwise the guard would have moved the hole rather than closed it.
    /// </summary>
    [Fact]
    public async Task AWriteThatWinsTheRace_IsStoredAndThenSweptByTheErasureItDelayed()
    {
        var memberId = await SeedMemberAsync();

        var writerHoldsTheLock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var erasureHasBeenAsked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var writerScope = _services.CreateScope();
        var digests = new DigestRepository(
            writerScope.ServiceProvider.GetRequiredService<CardiTrackDbContext>(),
            writerScope.ServiceProvider.GetRequiredService<IMemberWriteGuard>());
        var guard = writerScope.ServiceProvider.GetRequiredService<IMemberWriteGuard>();

        var stored = false;
        var writing = guard.WriteIfMemberLivesAsync(memberId, async _ =>
        {
            // The lock is held from here. Erasure is asked for now, and must not finish until this
            // delegate returns and the guard commits.
            writerHoldsTheLock.SetResult();
            await erasureHasBeenAsked.Task;
            stored = await digests.AddAsync(new DigestEntry
            {
                CardiMemberId = memberId,
                LocalDate = DateOnly.FromDateTime(DateTime.UtcNow),
                Audience = DigestAudience.Family,
                Headline = "A steady day",
                Text = "Nothing stood out today.",
                GeneratedAtUtc = DateTime.UtcNow,
                PromptVersion = 1,
            });
        });

        await writerHoldsTheLock.Task;
        var erasing = EraseAsync(memberId);
        erasureHasBeenAsked.SetResult();

        await writing;
        var report = await erasing;

        Assert.True(stored, "the writer held the lock first, so its row belongs");

        // The point of the whole ordering: erasure waited, and then took the row with everything
        // else. A cascade that had raced past would report zero here and leave the row live.
        Assert.Contains(report.RowsByTable, r => r is { Table: "DigestEntries", Rows: > 0 });
        await AssertNothingGeneratedSurvivesAsync(memberId);
    }

    /// <summary>
    /// The interleaving the lock exists for, and the only one a plain existence check would fail:
    /// the erasure has locked the member and deleted its rows but has <em>not committed</em>, and
    /// the write arrives in the middle of that.
    /// </summary>
    /// <remarks>
    /// The other cases in this file await the erasure to completion before writing, so a
    /// non-locking <c>WHERE EXISTS</c> would pass them — by the time the write runs there is
    /// genuinely no member row to find. This one is different: under READ COMMITTED an unlocked
    /// reader still sees the member, because the delete that removed it has not committed. A check
    /// says yes, the row inserts, the erasure commits on top of it, and the product is left
    /// holding health data for an erased member. Measured that way before this guard existed:
    /// one orphaned digest, zero members.
    /// <para>
    /// The erasure side is driven by hand rather than through <see cref="MemberErasureService"/>,
    /// because the service has no pause point and none is worth adding to production code for a
    /// test. What is driven is exactly what the service does, in its order: lock the member first,
    /// then delete. That the real service does take that lock first is what
    /// <see cref="AWriteThatWinsTheRace_IsStoredAndThenSweptByTheErasureItDelayed"/> proves — it
    /// could not block on a writer's <c>FOR KEY SHARE</c> otherwise.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AWriteArrivingWhileAnErasureIsUncommitted_WaitsForItAndThenRefuses()
    {
        var memberId = await SeedMemberAsync();

        await using var erasing = new NpgsqlConnection(_container.GetConnectionString());
        await erasing.OpenAsync();
        await using var erasure = await erasing.BeginTransactionAsync();

        await ExecuteAsync(erasing, erasure,
            $"""SELECT 1 FROM "CardiMembers" WHERE "Id" = '{memberId}' FOR UPDATE""");
        await ExecuteAsync(erasing, erasure,
            $"""DELETE FROM "DigestEntries" WHERE "CardiMemberId" = '{memberId}'""");
        await ExecuteAsync(erasing, erasure,
            $"""DELETE FROM "CardiMembers" WHERE "Id" = '{memberId}'""");

        // Uncommitted at this point, and that is the whole test.
        using var scope = _services.CreateScope();
        var digests = new DigestRepository(
            scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>(),
            scope.ServiceProvider.GetRequiredService<IMemberWriteGuard>());

        var writing = digests.AddAsync(new DigestEntry
        {
            CardiMemberId = memberId,
            LocalDate = DateOnly.FromDateTime(DateTime.UtcNow),
            Audience = DigestAudience.Daybook,
            Headline = "A steady day",
            Text = "Nothing stood out today.",
            GeneratedAtUtc = DateTime.UtcNow,
            PromptVersion = 1,
        });

        // It must block rather than decide. A guard that answered here — either way — would be
        // answering from a snapshot that still shows a member who is already gone.
        var decidedEarly = await Task.WhenAny(writing, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.NotSame(writing, decidedEarly);

        await erasure.CommitAsync();

        Assert.False(await writing, "the erasure it waited for had already removed the member");
        await AssertNothingGeneratedSurvivesAsync(memberId);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, DbTransaction transaction, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = (NpgsqlTransaction)transaction;
        await command.ExecuteNonQueryAsync();
    }

    // ── Harness ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs <paramref name="write"/> the way a generator reaches it: the member is read, the
    /// erasure completes while the "model call" is suspended, and only then is the write allowed to
    /// proceed and decide for itself.
    /// </summary>
    private async Task<bool> RaceAgainstErasureAsync(
        Guid memberId, Func<IServiceScope, CancellationToken, Task<bool>> write)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();

        // Step 1 of the shape this exists for: the generator has the member in hand, and every
        // decision it makes from here is made about a member it believes exists.
        var member = await db.CardiMembers.AsNoTracking().FirstOrDefaultAsync(m => m.Id == memberId);
        Assert.NotNull(member);

        // Step 2, collapsed: the erasure runs and commits entirely inside the model call.
        var report = await EraseAsync(memberId);
        Assert.Equal(0, await db.CardiMembers.CountAsync(m => m.Id == memberId));
        Assert.True(report.TotalRows > 0);

        // Step 3, on a context that has never been told any of that.
        db.ChangeTracker.Clear();
        return await write(scope, CancellationToken.None);
    }

    /// <summary>
    /// Every table an AI writer can reach, named one at a time. The cascade test's convention and
    /// for its reason: a loop over DbSets passes for a table nobody remembered.
    /// </summary>
    private async Task AssertNothingGeneratedSurvivesAsync(Guid memberId)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();

        Assert.Equal(0, await db.CardiMembers.CountAsync(x => x.Id == memberId));
        Assert.Equal(0, await db.DigestEntries.CountAsync(x => x.CardiMemberId == memberId));
        Assert.Equal(0, await db.RealtimeAssessments.CountAsync(x => x.CardiMemberId == memberId));
        Assert.Equal(0, await db.Set<MemberInsight>().CountAsync(x => x.CardiMemberId == memberId));
        Assert.Equal(0, await db.Set<MemberAdvise>().CountAsync(x => x.CardiMemberId == memberId));
        Assert.Equal(0, await db.MemberAdviseObservations.CountAsync(x => x.CardiMemberId == memberId));
        Assert.Equal(0, await db.MemberStatusLines.CountAsync(x => x.CardiMemberId == memberId));
        Assert.Equal(0, await db.MemberQuestionnaires.CountAsync(x => x.CardiMemberId == memberId));
        Assert.Equal(0, await db.MemberAiHolds.CountAsync(x => x.CardiMemberId == memberId));
        Assert.Equal(0, await db.NotificationDeliveries.CountAsync(x => x.CardiMemberId == memberId));
        Assert.Equal(0, await db.Alerts.CountAsync(x => x.CardiMemberId == memberId));
        Assert.Equal(0, await db.MemberChatSessions.CountAsync(x => x.CardiMemberId == memberId));
        Assert.Equal(0, await db.Reports.CountAsync(x => x.CardiMemberIds.Contains(memberId)));
    }

    private async Task<MemberErasureReport> EraseAsync(Guid memberId)
    {
        using var scope = _services.CreateScope();
        var sut = new MemberErasureService(
            scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>(),
            _photos,
            _reportStorage,
            _grantRevoker,
            NullLogger<MemberErasureService>.Instance);
        return await sut.EraseAsync(memberId);
    }

    /// <summary>
    /// The least a member can be and still be erasable: enough rows that the cascade reports a
    /// non-zero total, so a test cannot pass because nothing happened at all.
    /// </summary>
    private async Task<Guid> SeedMemberAsync()
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
            FirstName = "Margaret",
            LastName = "Doe",
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
        await db.SaveChangesAsync();

        return member.Id;
    }
}
