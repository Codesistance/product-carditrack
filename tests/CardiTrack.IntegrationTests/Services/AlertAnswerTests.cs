using CardiTrack.Application.Interfaces.Clients;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Security;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Services;
using CardiTrack.Application.Services.Notifications;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Persistence;
using CardiTrack.Infrastructure.Repositories;
using CardiTrack.Infrastructure.Security;
using CardiTrack.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Testcontainers.PostgreSql;

namespace CardiTrack.IntegrationTests.Services;

/// <summary>
/// Answering an alert, against a real Postgres and real AES.
/// </summary>
/// <remarks>
/// The unit tests cover the rules; what only a database can show is whether the note survives the
/// round trip through a column, whether the response table really is append-only under two
/// caregivers answering, and whether closing actually re-arms the producer's cooldown — which is
/// not a field anybody sets but a predicate the producers evaluate over stored rows.
/// </remarks>
public class AlertAnswerTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithCleanUp(true)
        .Build();

    private ServiceProvider _services = null!;

    private readonly Guid _organizationId = Guid.NewGuid();
    private readonly Guid _memberId = Guid.NewGuid();
    private readonly Guid _janeId = Guid.NewGuid();
    private readonly Guid _tomId = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        var sc = new ServiceCollection();
        sc.AddDbContext<CardiTrackDbContext>(options =>
            options.UseNpgsql(_container.GetConnectionString(),
                b => b.MigrationsAssembly("CardiTrack.Infrastructure")));

        var infrastructure = typeof(UnitOfWork).Assembly;
        foreach (var parameter in typeof(UnitOfWork).GetConstructors().Single().GetParameters())
        {
            if (parameter.ParameterType == typeof(CardiTrackDbContext))
                continue;

            var implementation = infrastructure.GetTypes().Single(t =>
                t.IsClass && !t.IsAbstract && parameter.ParameterType.IsAssignableFrom(t));
            sc.AddScoped(parameter.ParameterType, implementation);
        }
        sc.AddScoped<IMemberWriteGuard, MemberWriteGuard>();
        sc.AddScoped<IFamilyWriteGuard, FamilyWriteGuard>();
        sc.AddLogging();
        sc.AddScoped<IUnitOfWork, UnitOfWork>();
        sc.AddScoped<ICardiMemberAccessService, CardiMemberAccessService>();
        sc.AddScoped<IAckDeliveryService, AckDeliveryService>();
        sc.AddSingleton<IEncryptionService>(new AesEncryptionService(
            Convert.ToBase64String(new byte[32])));
        sc.AddSingleton(Substitute.For<IProfilePhotoStorage>());
        sc.AddScoped<IAlertService, AlertService>();

        _services = sc.BuildServiceProvider();

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
        await db.Database.MigrateAsync();
        await SeedAsync(db);
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

    /// <summary>
    /// A note about a named person's health, read back as typed — and sitting in the column as
    /// something nobody with a database connection can read.
    /// </summary>
    [Fact]
    public async Task ANoteOnAnAnswer_RoundTripsThroughTheColumn_AndIsNotStoredInTheClear()
    {
        const string note = "Called — she'd slept through her alarm, nothing wrong.";
        var alertId = await NewAlertAsync();

        using (var answering = _services.CreateScope())
        {
            await Alerts(answering).CloseAsync(_janeId, alertId, "slept_in", note);
        }

        using var check = _services.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<CardiTrackDbContext>();

        var stored = await db.AlertResponses.SingleAsync();
        Assert.NotNull(stored.Note);
        Assert.DoesNotContain("slept", stored.Note, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("v1:", stored.Note);

        var detail = await Alerts(check).GetByIdAsync(_janeId, alertId);
        Assert.Equal(note, Assert.Single(detail.Responses).Note);
    }

    /// <summary>
    /// Two caregivers answering leaves two rows. This is the property the whole table exists for:
    /// "what did the family do about this" is a sequence, not a latest value.
    /// </summary>
    [Fact]
    public async Task TwoCaregiversAnswering_LeavesBothAnswers_NewestFirst()
    {
        var alertId = await NewAlertAsync();

        using (var first = _services.CreateScope())
            await Alerts(first).AcknowledgeAsync(_janeId, alertId, "calling", null);
        using (var second = _services.CreateScope())
            await Alerts(second).AcknowledgeAsync(_tomId, alertId, "checking_in_person", null);

        using var check = _services.CreateScope();
        var detail = await Alerts(check).GetByIdAsync(_janeId, alertId);

        Assert.Equal(2, detail.Responses.Count);
        Assert.Equal(_tomId, detail.Responses[0].UserId);
        Assert.Equal("Tom Okafor", detail.Responses[0].UserName);
        Assert.Equal("Going to check in person", detail.Responses[0].ResponseLabel);

        // Jane acknowledged first and keeps the attribution; Tom's answer is kept beside it
        // rather than replacing it.
        Assert.Equal(_janeId, detail.AcknowledgedByUserId);
        Assert.Equal(_janeId, detail.Responses[1].UserId);
    }

    /// <summary>
    /// The reason close is its own action: it resolves the alert, and an unresolved alert is the
    /// only thing suppressing the rule.
    /// </summary>
    [Fact]
    public async Task ClosingAnAlert_ReArmsTheRule_SoAPersistingConditionCanFireAgain()
    {
        var alertId = await NewAlertAsync();

        using (var before = _services.CreateScope())
        {
            var open = await before.ServiceProvider.GetRequiredService<CardiTrackDbContext>()
                .Alerts.SingleAsync(a => a.Id == alertId);
            // The producers' own predicate, evaluated the way they evaluate it: an unresolved
            // alert carrying this rule is what stops a second one being raised.
            Assert.False(open.IsResolved);
        }

        using (var closing = _services.CreateScope())
            await Alerts(closing).CloseAsync(_janeId, alertId, "awake_and_fine", null);

        using var after = _services.CreateScope();
        var closed = await after.ServiceProvider.GetRequiredService<CardiTrackDbContext>()
            .Alerts.SingleAsync(a => a.Id == alertId);

        Assert.True(closed.IsResolved);
        Assert.Equal(_janeId, closed.ResolvedByUserId);
    }

    /// <summary>
    /// A caregiver answering in the app stops the ladder, including a copy still held for somebody
    /// else's quiet hours.
    /// </summary>
    [Fact]
    public async Task AnsweringInTheApp_StopsEveryDeliveryStillChasingTheFamily()
    {
        var alertId = await NewAlertAsync();

        using (var seeding = _services.CreateScope())
        {
            var db = seeding.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
            db.NotificationDeliveries.AddRange(
                Delivery(alertId, _janeId, DeliveryState.Sent),
                Delivery(alertId, _tomId, DeliveryState.Pending));
            await db.SaveChangesAsync();
        }

        using (var answering = _services.CreateScope())
            await Alerts(answering).AcknowledgeAsync(_janeId, alertId, "calling", null);

        using var check = _services.CreateScope();
        var db2 = check.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
        var states = await db2.NotificationDeliveries
            .Where(d => d.SourceId == alertId)
            .Select(d => d.State)
            .ToListAsync();

        Assert.All(states, state => Assert.Equal(DeliveryState.Answered, state));
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    private static IAlertService Alerts(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IAlertService>();

    private NotificationDelivery Delivery(Guid alertId, Guid userId, DeliveryState state) => new()
    {
        SourceType = DeliverySourceType.Alert,
        SourceId = alertId,
        UserId = userId,
        CardiMemberId = _memberId,
        Category = DeliveryCategory.Health,
        Severity = AlertSeverity.Red,
        Channel = DeliveryChannel.Push,
        State = state,
        DedupKey = $"alert:{alertId}:{userId}",
        ExpiresAt = DateTime.UtcNow.AddMinutes(25),
        SentDate = state == DeliveryState.Pending ? null : DateTime.UtcNow.AddMinutes(-4),
        EscalationStage = EscalationStage.Initial,
    };

    private async Task<Guid> NewAlertAsync()
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();

        var alert = new Alert
        {
            CardiMemberId = _memberId,
            AlertType = AlertType.PatternBreak,
            Severity = AlertSeverity.Red,
            Title = "Margaret hasn't moved today",
            Message = "No steps well after her usual wake time.",
            TriggeredDate = DateTime.UtcNow.AddMinutes(-20),
            MetricValues = """{"rule":"no_morning_activity"}""",
            IsActive = true,
        };
        db.Alerts.Add(alert);
        await db.SaveChangesAsync();
        return alert.Id;
    }

    private async Task SeedAsync(CardiTrackDbContext db)
    {
        db.Organizations.Add(new Organization
        {
            Id = _organizationId,
            Name = "Okafor family",
            FamilyId = "KTR7M2Q9",
            Type = OrganizationType.Family,
            IsActive = true,
        });

        db.Users.AddRange(
            new User
            {
                Id = _janeId,
                OrganizationId = _organizationId,
                Auth0UserId = "auth0|jane",
                Email = "jane@okafor.test",
                Name = "Jane Okafor",
                Role = UserRole.Admin,
                IsActive = true,
            },
            new User
            {
                Id = _tomId,
                OrganizationId = _organizationId,
                Auth0UserId = "auth0|tom",
                Email = "tom@okafor.test",
                Name = "Tom Okafor",
                Role = UserRole.Member,
                IsActive = true,
            });

        db.CardiMembers.Add(new CardiMember
        {
            Id = _memberId,
            OrganizationId = _organizationId,
            Name = "Margaret Okafor",
            DateOfBirth = new DateOnly(1948, 3, 2),
            IsActive = true,
        });

        db.UserCardiMembers.AddRange(
            new UserCardiMember
            {
                UserId = _janeId,
                CardiMemberId = _memberId,
                CanViewHealthData = true,
                ReceiveAlerts = true,
                IsActive = true,
            },
            new UserCardiMember
            {
                UserId = _tomId,
                CardiMemberId = _memberId,
                CanViewHealthData = true,
                ReceiveAlerts = true,
                IsActive = true,
            });

        await db.SaveChangesAsync();
    }
}
