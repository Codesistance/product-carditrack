using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Security;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Persistence;
using CardiTrack.Infrastructure.Repositories;
using CardiTrack.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Testcontainers.PostgreSql;

namespace CardiTrack.IntegrationTests.Services;

/// <summary>
/// Every ledger write reads the member's lines and writes back a summary derived from them, so two
/// at once are a read-modify-write race that only a database can show. The member's row lock
/// (<see cref="ICardiMemberRepository.LockForUpdateAsync"/>) is what closes it; these run the real
/// service against a real Postgres, concurrently, and read the tables back.
/// </summary>
public class MedicalLedgerConcurrencyTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithCleanUp(true)
        .Build();

    private ServiceProvider _services = null!;
    private readonly IEncryptionService _encryption = Substitute.For<IEncryptionService>();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        var sc = new ServiceCollection();
        sc.AddDbContext<CardiTrackDbContext>(options =>
            options.UseNpgsql(_container.GetConnectionString(),
                b => b.MigrationsAssembly("CardiTrack.Infrastructure")));

        // Every repository UnitOfWork takes, paired with its one implementation — the same
        // reflection CardiMemberCreationTransactionTests uses, for the same reason.
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

        _services = sc.BuildServiceProvider();

        using var scope = _services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>().Database.MigrateAsync();

        // A reversible stand-in for AES: the property under test is ordering, not cryptography.
        _encryption.Encrypt(Arg.Any<string>()).Returns(c => $"enc({c.Arg<string>()})");
        _encryption.Decrypt(Arg.Any<string>()).Returns(c =>
        {
            var value = c.Arg<string>();
            return value.StartsWith("enc(") && value.EndsWith(')')
                ? value[4..^1]
                : throw new FormatException("not ciphertext");
        });
    }

    public async Task DisposeAsync()
    {
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

    private MedicalEntryService CreateSut(IServiceProvider scope) => new(
        scope.GetRequiredService<IUnitOfWork>(),
        Substitute.For<ICardiMemberAccessService>(),
        _encryption,
        Substitute.For<INotificationGapResolver>());

    /// <summary>
    /// Without the lock each add reads a ledger missing the others' lines, and the last save's
    /// summary wins — every line in the table, most of them missing from the note the AI prompt
    /// and the older builds read.
    /// </summary>
    [Fact]
    public async Task LinesAddedAtOnce_AllReachTheSummary()
    {
        var memberId = await SeedMemberAsync(notes: null);
        var userId = Guid.NewGuid();

        await Task.WhenAll(Enumerable.Range(1, 6).Select(async i =>
        {
            using var scope = _services.CreateScope();
            await CreateSut(scope.ServiceProvider)
                .AddAsync(userId, memberId, MedicalEntryKind.Medication, $"Medicine {i}");
        }));

        using var read = _services.CreateScope();
        var db = read.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
        Assert.Equal(6, await db.MedicalEntries.CountAsync(e => e.CardiMemberId == memberId && e.RemovedAtUtc == null));

        var summary = _encryption.Decrypt((await db.CardiMembers.SingleAsync(m => m.Id == memberId)).MedicalNotes!);
        for (var i = 1; i <= 6; i++)
            Assert.Contains($"Medication: Medicine {i}", summary);
    }

    /// <summary>
    /// The first read of a note written before the ledger carries it over — and several screens
    /// opening at once must carry it over once, not once each.
    /// </summary>
    [Fact]
    public async Task FirstReadsAtOnce_CarryTheNoteOverOnce()
    {
        var memberId = await SeedMemberAsync(notes: "enc(Pacemaker fitted 2019)");

        await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            using var scope = _services.CreateScope();
            await CreateSut(scope.ServiceProvider).GetAsync(Guid.NewGuid(), memberId);
        }));

        using var read = _services.CreateScope();
        var db = read.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
        Assert.Equal(1, await db.MedicalEntries.CountAsync(e => e.CardiMemberId == memberId));
    }

    /// <summary>Taken outside a transaction the lock would be released at once and protect nothing.</summary>
    [Fact]
    public async Task TheLock_RefusesToBeTakenOutsideATransaction()
    {
        var memberId = await SeedMemberAsync(notes: null);

        using var scope = _services.CreateScope();
        var members = scope.ServiceProvider.GetRequiredService<ICardiMemberRepository>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => members.LockForUpdateAsync(memberId));
    }

    private async Task<Guid> SeedMemberAsync(string? notes)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
        var organization = new Organization { Name = "Doe family", Type = OrganizationType.Family };
        db.Organizations.Add(organization);
        var member = new CardiMember
        {
            OrganizationId = organization.Id,
            Name = "Margaret Doe",
            DateOfBirth = new DateOnly(1948, 3, 1),
            MedicalNotes = notes,
            IsActive = true,
        };
        db.CardiMembers.Add(member);
        await db.SaveChangesAsync();
        return member.Id;
    }
}
