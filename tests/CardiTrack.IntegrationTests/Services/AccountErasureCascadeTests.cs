using CardiTrack.Application.Interfaces.Clients;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Persistence;
using CardiTrack.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Testcontainers.PostgreSql;

namespace CardiTrack.IntegrationTests.Services;

/// <summary>
/// Whether closing an account erases the right people and only the right people.
/// </summary>
/// <remarks>
/// The member cascade is covered by <see cref="MemberErasureCascadeTests"/>; what is at stake
/// here is the decision made before it runs. Erase too little and a departing caregiver's
/// forgotten member keeps two years of readings nobody watches. Erase too much and a second
/// family loses a record they never asked to lose — the worse of the two, and the one a passing
/// "everything is gone" assertion would happily hide. So both directions are asserted against a
/// real Postgres.
/// </remarks>
public class AccountErasureCascadeTests : IAsyncLifetime
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
        sc.AddScoped<ITimeSeriesPartitionService, TimeSeriesPartitionService>();
        sc.AddLogging();
        _services = sc.BuildServiceProvider();

        using var scope = _services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>().Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<ITimeSeriesPartitionService>()
            .EnsureUpcomingPartitionsAsync(daysAhead: 7);
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _container.DisposeAsync();
    }

    /// <summary>
    /// The ordinary case: the members nobody else watches go, and so does the account — down to
    /// the household it was the last member of, and that household's subscription.
    /// </summary>
    [Fact]
    public async Task ClosingASoleCaregiversAccount_ErasesTheMemberAndTheAccount()
    {
        var seed = await SeedAsync();

        var report = await EraseAsync(seed.UserId);

        Assert.Equal(
            new HashSet<Guid> { seed.SoleMemberId, seed.RemovedMemberId },
            report.MembersErased.ToHashSet());

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();

        Assert.Equal(0, await db.CardiMembers.CountAsync(x => x.Id == seed.SoleMemberId));
        Assert.Equal(0, await db.ActivityLogs.CountAsync(x => x.CardiMemberId == seed.SoleMemberId));
        Assert.Equal(0, await db.Users.CountAsync(x => x.Id == seed.UserId));
        Assert.Equal(0, await db.Organizations.CountAsync(x => x.Id == seed.OrganizationId));
        Assert.Equal(0, await db.Subscriptions.CountAsync(x => x.OrganizationId == seed.OrganizationId));
        Assert.Equal(0, await db.PushDeviceTokens.CountAsync(x => x.UserId == seed.UserId));
        Assert.Equal(0, await db.NotificationPreferences.CountAsync(x => x.UserId == seed.UserId));
        Assert.Equal(0, await db.ExportConsents.CountAsync(x => x.OwnerUserId == seed.UserId));
        Assert.Equal(0, await db.Reports.CountAsync(x => x.OwnerUserId == seed.UserId));
        Assert.Equal(0, await db.CardiMemberCreationKeys.CountAsync(x => x.UserId == seed.UserId));
        Assert.Equal(0, await db.UserCardiMembers.CountAsync(x => x.UserId == seed.UserId));
        Assert.Equal(0, await db.MetricAlarms.CountAsync(
            x => x.OrganizationId == seed.OrganizationId && x.CardiMemberId == null));
    }

    /// <summary>
    /// The case that must not over-reach. A member two caregivers watch belongs to neither of
    /// them: the departing caregiver's link goes, and the member, their readings and the other
    /// caregiver's link all stay.
    /// </summary>
    [Fact]
    public async Task ClosingOneCaregiversAccount_LeavesAMemberSomeoneElseStillWatches()
    {
        var seed = await SeedAsync();

        var report = await EraseAsync(seed.UserId);

        Assert.Equal([seed.SharedMemberId], report.MembersReleased);
        Assert.DoesNotContain(seed.SharedMemberId, report.MembersErased);

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();

        Assert.Equal(1, await db.CardiMembers.CountAsync(x => x.Id == seed.SharedMemberId));
        Assert.Equal(1, await db.ActivityLogs.CountAsync(x => x.CardiMemberId == seed.SharedMemberId));
        Assert.Equal(1, await db.UserCardiMembers.CountAsync(
            x => x.CardiMemberId == seed.SharedMemberId && x.UserId == seed.OtherUserId));
        Assert.Equal(0, await db.UserCardiMembers.CountAsync(
            x => x.CardiMemberId == seed.SharedMemberId && x.UserId == seed.UserId));
    }

    /// <summary>
    /// A member the departing caregiver had already removed — an inactive link, and nobody else
    /// on the member at all. Nobody is left to be told anything about them, so they go too.
    /// </summary>
    [Fact]
    public async Task ClosingAnAccount_ErasesAMemberTheyHadAlreadyRemoved()
    {
        var seed = await SeedAsync();

        var report = await EraseAsync(seed.UserId);

        Assert.Contains(seed.RemovedMemberId, report.MembersErased);

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
        Assert.Equal(0, await db.CardiMembers.CountAsync(x => x.Id == seed.RemovedMemberId));
    }

    /// <summary>
    /// Runbook row 40: the acknowledgement and the answer belong to the member, so the caregiver's
    /// name comes off them rather than the rows going with the caregiver. Deleting them would take
    /// a surviving member's alert history with a departing relative.
    /// </summary>
    [Fact]
    public async Task ClosingAnAccount_NullsTheCaregiverOffASurvivingMembersAlert()
    {
        var seed = await SeedAsync();

        await EraseAsync(seed.UserId);

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();

        var alert = await db.Alerts.SingleAsync(a => a.CardiMemberId == seed.SharedMemberId);
        Assert.Null(alert.AcknowledgedByUserId);

        var questionnaire = await db.MemberQuestionnaires
            .SingleAsync(q => q.CardiMemberId == seed.SharedMemberId);
        Assert.Null(questionnaire.AnsweredByUserId);
    }

    /// <summary>
    /// The organisation outlives the user while anyone else is in it — and so do the account-wide
    /// alarm defaults and the subscription, which are the remaining household's, not the
    /// departing caregiver's.
    /// </summary>
    [Fact]
    public async Task ClosingOneOfTwoAccountsInAHousehold_KeepsTheOrganisation()
    {
        var (organizationId, leaving, staying) = await SeedSharedHouseholdAsync();

        await EraseAsync(leaving);

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();

        Assert.Equal(0, await db.Users.CountAsync(x => x.Id == leaving));
        Assert.Equal(1, await db.Users.CountAsync(x => x.Id == staying));
        Assert.Equal(1, await db.Organizations.CountAsync(x => x.Id == organizationId));
        Assert.Equal(1, await db.Subscriptions.CountAsync(x => x.OrganizationId == organizationId));
        Assert.Equal(1, await db.MetricAlarms.CountAsync(
            x => x.OrganizationId == organizationId && x.CardiMemberId == null));
    }

    /// <summary>Two caregivers sharing one household, so the organisation has somebody left.</summary>
    private async Task<(Guid OrganizationId, Guid Leaving, Guid Staying)> SeedSharedHouseholdAsync()
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();

        var organization = new Organization { Name = "Reid family", Type = OrganizationType.Family };
        db.Organizations.Add(organization);

        var leaving = NewUser(organization.Id, "Anna Reid");
        var staying = NewUser(organization.Id, "Tom Reid");
        db.Users.AddRange(leaving, staying);

        db.Subscriptions.Add(new Subscription
        {
            OrganizationId = organization.Id,
            Tier = SubscriptionTier.Complete,
            Status = SubscriptionStatus.Trial,
            StartDate = DateTime.UtcNow.AddDays(-10),
        });
        db.MetricAlarms.Add(new MetricAlarm
        {
            OrganizationId = organization.Id,
            CardiMemberId = null,
            Name = "Account default",
        });

        await db.SaveChangesAsync();
        return (organization.Id, leaving.Id, staying.Id);
    }

    /// <summary>
    /// Re-running the cascade over an account that is already gone is how a failed run recovers,
    /// so it has to be a no-op rather than a throw.
    /// </summary>
    [Fact]
    public async Task ErasingAnAccountTwice_IsAQuietNoOpTheSecondTime()
    {
        var seed = await SeedAsync();

        await EraseAsync(seed.UserId);
        var second = await EraseAsync(seed.UserId);

        Assert.Empty(second.MembersErased);
        Assert.Empty(second.RowsByTable);
        Assert.Empty(second.OrphanedObjects);
    }

    private async Task<AccountErasureReport> EraseAsync(Guid userId)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();

        var members = new MemberErasureService(
            db, _photos, _reportStorage, NullLogger<MemberErasureService>.Instance);
        var sut = new AccountErasureService(
            db, members, _reportStorage, NullLogger<AccountErasureService>.Instance);

        return await sut.EraseAsync(userId);
    }

    private sealed record Seed(
        Guid OrganizationId,
        Guid UserId,
        Guid OtherUserId,
        Guid SoleMemberId,
        Guid SharedMemberId,
        Guid RemovedMemberId);

    /// <summary>
    /// One household, two caregivers, three members — one watched only by the departing
    /// caregiver, one watched by both, and one the departing caregiver had already removed.
    /// Synthesised figures throughout; this repository is public, so no test carries a real
    /// wearer's reading.
    /// </summary>
    private async Task<Seed> SeedAsync()
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();

        var organization = new Organization { Name = "Doe family", Type = OrganizationType.Family };
        db.Organizations.Add(organization);

        var leaving = new User
        {
            OrganizationId = organization.Id,
            Email = $"leaving-{Guid.NewGuid():N}@example.com",
            Name = "Jane Doe",
        };
        // A second household, because the caregiver who stays is an invited relative rather than
        // a second seat on this account — which is what makes the departing caregiver the last
        // one out of their own organisation while the shared member still has somebody.
        var otherHousehold = new Organization { Name = "Reid family", Type = OrganizationType.Family };
        db.Organizations.Add(otherHousehold);

        var staying = new User
        {
            OrganizationId = otherHousehold.Id,
            Email = $"staying-{Guid.NewGuid():N}@example.com",
            Name = "Paul Doe",
        };
        db.Users.AddRange(leaving, staying);

        var sole = NewMember(organization.Id, "Margaret Doe");
        var shared = NewMember(organization.Id, "Arthur Doe");
        var removed = NewMember(organization.Id, "Edith Doe");
        db.CardiMembers.AddRange(sole, shared, removed);
        await db.SaveChangesAsync();

        db.UserCardiMembers.AddRange(
            NewLink(leaving.Id, sole.Id, active: true),
            NewLink(leaving.Id, shared.Id, active: true),
            NewLink(staying.Id, shared.Id, active: true),
            NewLink(leaving.Id, removed.Id, active: false));

        foreach (var memberId in new[] { sole.Id, shared.Id, removed.Id })
        {
            db.ActivityLogs.Add(new ActivityLog
            {
                CardiMemberId = memberId,
                Date = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)),
                Steps = 4200,
            });
        }

        // On the shared member, so the assertion is that a surviving member keeps their alert
        // with the departing caregiver's name taken off it.
        db.Alerts.Add(new Alert
        {
            CardiMemberId = shared.Id,
            Title = "Quieter than usual",
            Message = "Fewer steps than their usual pattern.",
            AcknowledgedByUserId = leaving.Id,
        });
        db.MemberQuestionnaires.Add(new MemberQuestionnaire
        {
            CardiMemberId = shared.Id,
            QuestionText = "Did they sleep well last night?",
            AnsweredByUserId = leaving.Id,
        });

        db.Subscriptions.Add(new Subscription
        {
            OrganizationId = organization.Id,
            Tier = SubscriptionTier.Complete,
            Status = SubscriptionStatus.Trial,
            StartDate = DateTime.UtcNow.AddDays(-10),
        });
        db.MetricAlarms.Add(new MetricAlarm
        {
            OrganizationId = organization.Id,
            CardiMemberId = null,
            Name = "Account default",
        });
        db.PushDeviceTokens.Add(new PushDeviceToken
        {
            UserId = leaving.Id,
            DeviceId = Guid.NewGuid().ToString("N"),
            Platform = DevicePlatform.Android,
            Token = "encrypted-token",
            TokenFingerprint = Guid.NewGuid().ToString("N"),
            LastSeenDate = DateTime.UtcNow,
        });
        db.NotificationPreferences.Add(new NotificationPreference { UserId = leaving.Id });
        db.ExportConsents.Add(new ExportConsent
        {
            OwnerUserId = leaving.Id,
            CardiMemberIds = [shared.Id],
            DateRangeFrom = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-7)),
            DateRangeTo = DateOnly.FromDateTime(DateTime.UtcNow),
            PolicyVersion = "2026-09-01",
        });
        db.Reports.Add(new Report
        {
            OwnerUserId = leaving.Id,
            CardiMemberIds = [shared.Id],
            ObjectName = "reports/seeded-export.pdf",
        });
        db.CardiMemberCreationKeys.Add(new CardiMemberCreationKey
        {
            UserId = leaving.Id,
            Key = Guid.NewGuid().ToString("N"),
            CardiMemberId = sole.Id,
        });

        await db.SaveChangesAsync();

        return new Seed(organization.Id, leaving.Id, staying.Id, sole.Id, shared.Id, removed.Id);
    }

    private static User NewUser(Guid organizationId, string name) => new()
    {
        OrganizationId = organizationId,
        Email = $"caregiver-{Guid.NewGuid():N}@example.com",
        Name = name,
    };

    private static CardiMember NewMember(Guid organizationId, string name) => new()
    {
        OrganizationId = organizationId,
        Name = name,
        DateOfBirth = new DateOnly(1948, 4, 2),
        Gender = Gender.Female,
        IsActive = true,
    };

    private static UserCardiMember NewLink(Guid userId, Guid memberId, bool active) => new()
    {
        UserId = userId,
        CardiMemberId = memberId,
        RelationshipType = RelationshipType.Parent,
        IsPrimaryCaregiver = true,
        CanViewHealthData = true,
        ReceiveAlerts = true,
        IsActive = active,
    };
}
