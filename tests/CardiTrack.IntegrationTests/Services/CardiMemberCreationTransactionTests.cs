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
