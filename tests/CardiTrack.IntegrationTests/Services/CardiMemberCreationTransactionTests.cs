using System.Reflection;
using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.Interfaces.Clients;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Security;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Persistence;
using CardiTrack.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Testcontainers.PostgreSql;

namespace CardiTrack.IntegrationTests.Services;

/// <summary>
/// Member creation is two saves — the member, then the caregiver's link to it — inside one
/// transaction. A substitute unit of work can only show the service <em>asked</em> for a
/// rollback; whether the first save is actually undone is a database question, so this runs
/// against a real Postgres and reads the tables back.
/// </summary>
public class CardiMemberCreationTransactionTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
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

        // UnitOfWork takes every repository. They all construct from the DbContext alone, so
        // register each constructor parameter's interface against the one Infrastructure class
        // that implements it — the same pairing every host's Program.cs spells out by hand.
        var infrastructure = typeof(UnitOfWork).Assembly;
        foreach (var parameter in typeof(UnitOfWork).GetConstructors().Single().GetParameters())
        {
            if (parameter.ParameterType == typeof(CardiTrackDbContext))
                continue;

            var implementation = infrastructure.GetTypes().Single(t =>
                t.IsClass && !t.IsAbstract && parameter.ParameterType.IsAssignableFrom(t));
            sc.AddScoped(parameter.ParameterType, implementation);
        }
        sc.AddScoped<IUnitOfWork, UnitOfWork>();

        _services = sc.BuildServiceProvider();

        using var scope = _services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>().Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _container.DisposeAsync();
    }

    [Fact]
    public async Task AFailedCaregiverLink_LeavesNoMemberBehind()
    {
        var organizationId = await SeedOrganizationAsync();
        var nobody = Guid.NewGuid(); // no Users row — the link's foreign key refuses it

        using (var scope = _services.CreateScope())
        {
            var sut = CreateSut(scope.ServiceProvider.GetRequiredService<IUnitOfWork>());

            await Assert.ThrowsAsync<DbUpdateException>(
                () => sut.CreateCardiMemberAsync(organizationId, nobody, BuildRequest()));
        }

        // A fresh context: the failed one still tracks what it tried to write.
        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
            Assert.Equal(0, await db.CardiMembers.CountAsync(m => m.OrganizationId == organizationId));
            Assert.Equal(0, await db.UserCardiMembers.CountAsync(l => l.UserId == nobody));
        }
    }

    [Fact]
    public async Task ASuccessfulCreate_CommitsBothRows()
    {
        var organizationId = await SeedOrganizationAsync();
        var userId = await SeedUserAsync(organizationId);

        Guid memberId;
        using (var scope = _services.CreateScope())
        {
            var sut = CreateSut(scope.ServiceProvider.GetRequiredService<IUnitOfWork>());
            var response = await sut.CreateCardiMemberAsync(organizationId, userId, BuildRequest());
            memberId = response.Id;
        }

        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
            Assert.Equal(1, await db.CardiMembers.CountAsync(m => m.Id == memberId));
            Assert.Equal(1, await db.UserCardiMembers.CountAsync(l => l.CardiMemberId == memberId && l.UserId == userId));
        }
    }

    /// <summary>
    /// The case the whole design exists for: a commit the database accepted whose acknowledgement
    /// never reached the phone. The caregiver is looking at a failure while the member exists, and
    /// taps Continue again.
    /// </summary>
    [Fact]
    public async Task ARetryUnderTheSameKey_ReturnsTheFirstMember_AndAddsNoSecond()
    {
        var organizationId = await SeedOrganizationAsync();
        var userId = await SeedUserAsync(organizationId);
        var key = Guid.NewGuid().ToString("N");

        Guid first, second;
        using (var scope = _services.CreateScope())
        {
            var sut = CreateSut(scope.ServiceProvider.GetRequiredService<IUnitOfWork>());
            first = (await sut.CreateCardiMemberAsync(organizationId, userId, BuildRequest(), key)).Id;
        }

        // A separate scope, because a retry is a separate request.
        using (var scope = _services.CreateScope())
        {
            var sut = CreateSut(scope.ServiceProvider.GetRequiredService<IUnitOfWork>());
            second = (await sut.CreateCardiMemberAsync(organizationId, userId, BuildRequest(), key)).Id;
        }

        Assert.Equal(first, second);

        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
            Assert.Equal(1, await db.CardiMembers.CountAsync(m => m.OrganizationId == organizationId));
            Assert.Equal(1, await db.UserCardiMembers.CountAsync(l => l.UserId == userId));
        }
    }

    /// <summary>
    /// The key commits with the member or not at all — which is what makes the retry above a
    /// question with an answer rather than a guess. Here the caregiver link fails, so nothing
    /// should survive, the key included: a later attempt must be free to create the member.
    /// </summary>
    [Fact]
    public async Task AFailedCreate_LeavesNoKeyBehind()
    {
        var organizationId = await SeedOrganizationAsync();
        var nobody = Guid.NewGuid(); // no Users row — the link's foreign key refuses it
        var key = Guid.NewGuid().ToString("N");

        using (var scope = _services.CreateScope())
        {
            var sut = CreateSut(scope.ServiceProvider.GetRequiredService<IUnitOfWork>());
            await Assert.ThrowsAsync<DbUpdateException>(
                () => sut.CreateCardiMemberAsync(organizationId, nobody, BuildRequest(), key));
        }

        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
            Assert.Equal(0, await db.CardiMemberCreationKeys.CountAsync(k => k.Key == key));
        }
    }

    /// <summary>
    /// A key names one attempt, not one person. Two members with identical details and different
    /// keys are two members — the twins case the natural-key alternative would have got wrong.
    /// </summary>
    [Fact]
    public async Task ADifferentKey_CreatesASecondMember()
    {
        var organizationId = await SeedOrganizationAsync();
        var userId = await SeedUserAsync(organizationId);

        using (var scope = _services.CreateScope())
        {
            var sut = CreateSut(scope.ServiceProvider.GetRequiredService<IUnitOfWork>());
            await sut.CreateCardiMemberAsync(
                organizationId, userId, BuildRequest(), Guid.NewGuid().ToString("N"));
        }

        using (var scope = _services.CreateScope())
        {
            var sut = CreateSut(scope.ServiceProvider.GetRequiredService<IUnitOfWork>());
            await sut.CreateCardiMemberAsync(
                organizationId, userId, BuildRequest(), Guid.NewGuid().ToString("N"));
        }

        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
            Assert.Equal(2, await db.CardiMembers.CountAsync(m => m.OrganizationId == organizationId));
        }
    }

    /// <summary>
    /// No key, no protection — stated as a test so the opt-in is a decision on the record rather
    /// than something a reader has to infer. An installed app that predates the header still
    /// creates members exactly as it did.
    /// </summary>
    [Fact]
    public async Task WithoutAKey_TwoCreatesMakeTwoMembers()
    {
        var organizationId = await SeedOrganizationAsync();
        var userId = await SeedUserAsync(organizationId);

        for (var i = 0; i < 2; i++)
        {
            using var scope = _services.CreateScope();
            var sut = CreateSut(scope.ServiceProvider.GetRequiredService<IUnitOfWork>());
            await sut.CreateCardiMemberAsync(organizationId, userId, BuildRequest());
        }

        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
            Assert.Equal(2, await db.CardiMembers.CountAsync(m => m.OrganizationId == organizationId));
        }
    }

    /// <summary>
    /// One caregiver's key is not another's. Scoping the lookup by user is what stops a guessed or
    /// reused key from handing someone a member that is not theirs.
    /// </summary>
    [Fact]
    public async Task TheSameKeyUnderADifferentCaregiver_CreatesItsOwnMember()
    {
        var organizationId = await SeedOrganizationAsync();
        var first = await SeedUserAsync(organizationId);
        var second = await SeedUserAsync(organizationId);
        var key = Guid.NewGuid().ToString("N");

        Guid a, b;
        using (var scope = _services.CreateScope())
        {
            var sut = CreateSut(scope.ServiceProvider.GetRequiredService<IUnitOfWork>());
            a = (await sut.CreateCardiMemberAsync(organizationId, first, BuildRequest(), key)).Id;
        }

        using (var scope = _services.CreateScope())
        {
            var sut = CreateSut(scope.ServiceProvider.GetRequiredService<IUnitOfWork>());
            b = (await sut.CreateCardiMemberAsync(organizationId, second, BuildRequest(), key)).Id;
        }

        Assert.NotEqual(a, b);
    }

    /// <summary>
    /// The attempt landed and its member was later removed. Retrying under the same key is then a
    /// request to add that person again — which must work, rather than colliding with the spent
    /// key on the unique index and failing with a 500 nobody can act on.
    /// </summary>
    [Fact]
    public async Task AKeyWhoseMemberIsGone_CreatesAgainRatherThanFailing()
    {
        var organizationId = await SeedOrganizationAsync();
        var userId = await SeedUserAsync(organizationId);
        var key = Guid.NewGuid().ToString("N");

        Guid first;
        using (var scope = _services.CreateScope())
        {
            var sut = CreateSut(scope.ServiceProvider.GetRequiredService<IUnitOfWork>());
            first = (await sut.CreateCardiMemberAsync(organizationId, userId, BuildRequest(), key)).Id;
        }

        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
            db.UserCardiMembers.RemoveRange(db.UserCardiMembers.Where(l => l.CardiMemberId == first));
            db.CardiMembers.RemoveRange(db.CardiMembers.Where(m => m.Id == first));
            await db.SaveChangesAsync();
        }

        Guid second;
        using (var scope = _services.CreateScope())
        {
            var sut = CreateSut(scope.ServiceProvider.GetRequiredService<IUnitOfWork>());
            second = (await sut.CreateCardiMemberAsync(organizationId, userId, BuildRequest(), key)).Id;
        }

        Assert.NotEqual(first, second);

        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
            Assert.Equal(1, await db.CardiMemberCreationKeys.CountAsync(k => k.Key == key));
        }
    }

    private static CardiMemberService CreateSut(IUnitOfWork unitOfWork)
    {
        var encryption = Substitute.For<IEncryptionService>();
        encryption.Encrypt(Arg.Any<string>()).Returns(c => c.Arg<string>());

        return new CardiMemberService(
            unitOfWork,
            Substitute.For<ICardiMemberAccessService>(),
            encryption,
            Substitute.For<INotificationGapResolver>(),
            Substitute.For<IProfilePhotoProcessor>(),
            Substitute.For<IProfilePhotoStorage>());
    }

    private static CreateCardiMemberRequest BuildRequest() => new()
    {
        Name = "Margaret Doe",
        DateOfBirth = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-78)),
        Gender = Gender.Female,
        RelationshipType = RelationshipType.Parent,
        IsPrimaryCaregiver = true,
    };

    private async Task<Guid> SeedOrganizationAsync()
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
        var organization = new Organization { Name = "Doe family", Type = OrganizationType.Family };
        db.Organizations.Add(organization);
        await db.SaveChangesAsync();
        return organization.Id;
    }

    private async Task<Guid> SeedUserAsync(Guid organizationId)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
        var user = new User
        {
            OrganizationId = organizationId,
            Email = $"caregiver-{Guid.NewGuid():N}@example.com",
            Name = "Jane Doe",
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }
}
