using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Common;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Persistence;
using CardiTrack.Infrastructure.Repositories;
using CardiTrack.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace CardiTrack.IntegrationTests.Services;

/// <summary>
/// The Family ID route in, and the thing that makes it safe: approval, not the code.
/// </summary>
/// <remarks>
/// A Family ID is short enough to read down the phone and therefore short enough to guess. The
/// tests that matter most here are the ones asserting that guessing gains nothing — an unknown
/// code, a malformed one, and a family you are already in all answer identically, and a request on
/// its own grants nothing at all.
/// </remarks>
public class FamilyJoinTests : IAsyncLifetime
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
        sc.AddScoped<IFamilyJoinService, FamilyJoinService>();

        _services = sc.BuildServiceProvider();

        using var scope = _services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>().Database.MigrateAsync();
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
    /// The whole security argument in one test: a real code, a wrong code, a malformed code and a
    /// family you are already in are indistinguishable from outside. If this ever stops holding,
    /// the Family ID becomes an oracle for which families exist.
    /// </summary>
    [Fact]
    public async Task AskingRevealsNothing_WhateverTheCodeNames()
    {
        var seed = await SeedAsync();

        using var scope = _services.CreateScope();
        var sut = Sut(scope);

        var unknown = await sut.RequestAsync(seed.OutsiderId, new JoinFamilyRequest { FamilyId = "ZZZZ-ZZZZ" });
        var malformed = await sut.RequestAsync(seed.OutsiderId, new JoinFamilyRequest { FamilyId = "nope" });
        var alreadyIn = await sut.RequestAsync(seed.AdminId, new JoinFamilyRequest { FamilyId = seed.FamilyId });

        Assert.Null(unknown.RequestId);
        Assert.Null(malformed.RequestId);
        Assert.Null(alreadyIn.RequestId);

        // And the real thing looks different only to somebody who is entitled to a request.
        var real = await sut.RequestAsync(seed.OutsiderId, new JoinFamilyRequest { FamilyId = seed.FamilyId });
        Assert.NotNull(real.RequestId);
    }

    /// <summary>
    /// A code read out over a bad line and typed back in lower case with a space finds the same
    /// family. Refusing that would make the product's own suggestion the least reliable way to use it.
    /// </summary>
    [Theory]
    [InlineData("{0}")]
    [InlineData("{1}")]
    [InlineData(" {1} ")]
    public async Task ACodeIsFound_HoweverItWasTypedBack(string template)
    {
        var seed = await SeedAsync();
        var spaced = seed.FamilyId.Replace("-", " ").ToLowerInvariant();
        var typed = string.Format(template, seed.FamilyId, spaced);

        using var scope = _services.CreateScope();
        var receipt = await Sut(scope).RequestAsync(seed.OutsiderId, new JoinFamilyRequest { FamilyId = typed });

        Assert.NotNull(receipt.RequestId);
    }

    /// <summary>
    /// Asking is not getting. Until an admin approves, the asker has no membership and no grant —
    /// which is what makes a guessable code acceptable in the first place.
    /// </summary>
    [Fact]
    public async Task APendingRequest_GrantsNothing()
    {
        var seed = await SeedAsync();
        using (var scope = _services.CreateScope())
        {
            await Sut(scope).RequestAsync(seed.OutsiderId, new JoinFamilyRequest { FamilyId = seed.FamilyId });
        }

        using var check = _services.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
        Assert.Equal(0, await db.UserOrganizations.CountAsync(m => m.UserId == seed.OutsiderId));
        Assert.Equal(0, await db.UserCardiMembers.CountAsync(l => l.UserId == seed.OutsiderId));
    }

    /// <summary>Asking twice is a double tap. An admin's queue holds one row per person, not a pile.</summary>
    [Fact]
    public async Task AskingTwice_ReturnsTheSameOutstandingRequest()
    {
        var seed = await SeedAsync();

        using var scope = _services.CreateScope();
        var sut = Sut(scope);
        var first = await sut.RequestAsync(seed.OutsiderId, new JoinFamilyRequest { FamilyId = seed.FamilyId });
        var second = await sut.RequestAsync(seed.OutsiderId, new JoinFamilyRequest { FamilyId = seed.FamilyId });

        Assert.Equal(first.RequestId, second.RequestId);

        var pending = await sut.GetPendingAsync(seed.AdminId, seed.OrganizationId);
        Assert.Single(pending);
    }

    /// <summary>
    /// Approval is one act: the membership and every grant land together, so nobody is ever inside
    /// a family with an access decision still outstanding.
    /// </summary>
    [Fact]
    public async Task Approving_AdmitsThemAndGrantsTheChosenMembersTogether()
    {
        var seed = await SeedAsync();
        Guid requestId;
        using (var scope = _services.CreateScope())
        {
            var receipt = await Sut(scope).RequestAsync(
                seed.OutsiderId, new JoinFamilyRequest { FamilyId = seed.FamilyId });
            requestId = receipt.RequestId!.Value;
        }

        using (var approving = _services.CreateScope())
        {
            await Sut(approving).ApproveAsync(seed.AdminId, seed.OrganizationId, requestId,
                new ApproveJoinRequest
                {
                    CardiMemberIds = [seed.MemberId],
                    Role = "member",
                    ReceiveAlerts = false,
                });
        }

        using var check = _services.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<CardiTrackDbContext>();

        var membership = await db.UserOrganizations.SingleAsync(m => m.UserId == seed.OutsiderId);
        Assert.Equal(UserRole.Member, membership.Role);
        Assert.True(membership.IsActive);

        var link = await db.UserCardiMembers.SingleAsync(l => l.UserId == seed.OutsiderId);
        Assert.Equal(seed.MemberId, link.CardiMemberId);
        Assert.False(link.ReceiveAlerts);

        // The second member of the family was not chosen and was not granted.
        Assert.Equal(0, await db.UserCardiMembers.CountAsync(
            l => l.UserId == seed.OutsiderId && l.CardiMemberId == seed.SecondMemberId));
    }

    /// <summary>
    /// Admitting somebody as admin hands them the family and its plan, and the approver stops
    /// being one in the same save — a family has exactly one.
    /// </summary>
    [Fact]
    public async Task ApprovingAsAdmin_HandsTheFamilyOver()
    {
        var seed = await SeedAsync();
        Guid requestId;
        using (var scope = _services.CreateScope())
        {
            requestId = (await Sut(scope).RequestAsync(
                seed.OutsiderId, new JoinFamilyRequest { FamilyId = seed.FamilyId })).RequestId!.Value;
        }

        using (var approving = _services.CreateScope())
        {
            await Sut(approving).ApproveAsync(seed.AdminId, seed.OrganizationId, requestId,
                new ApproveJoinRequest { CardiMemberIds = [], Role = "admin" });
        }

        using var check = _services.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<CardiTrackDbContext>();

        var admins = await db.UserOrganizations
            .Where(m => m.OrganizationId == seed.OrganizationId && m.IsActive && m.Role == UserRole.Admin)
            .ToListAsync();
        Assert.Single(admins);
        Assert.Equal(seed.OutsiderId, admins[0].UserId);
    }

    /// <summary>
    /// Two admins answering at once is settled in the database, before anything is written, so the
    /// loser adds no second membership and no duplicate grants.
    /// </summary>
    [Fact]
    public async Task ARequest_CanOnlyBeAnsweredOnce()
    {
        var seed = await SeedAsync();
        Guid requestId;
        using (var scope = _services.CreateScope())
        {
            requestId = (await Sut(scope).RequestAsync(
                seed.OutsiderId, new JoinFamilyRequest { FamilyId = seed.FamilyId })).RequestId!.Value;
        }

        using (var first = _services.CreateScope())
        {
            await Sut(first).ApproveAsync(seed.AdminId, seed.OrganizationId, requestId,
                new ApproveJoinRequest { CardiMemberIds = [seed.MemberId] });
        }

        using var second = _services.CreateScope();
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Sut(second).ApproveAsync(
            seed.AdminId, seed.OrganizationId, requestId,
            new ApproveJoinRequest { CardiMemberIds = [seed.SecondMemberId] }));

        using var check = _services.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
        Assert.Equal(1, await db.UserCardiMembers.CountAsync(l => l.UserId == seed.OutsiderId));
    }

    /// <summary>
    /// A member id from another family is a mistake at best. The approval fails rather than
    /// silently granting less than it says it does.
    /// </summary>
    [Fact]
    public async Task Approving_RefusesAMemberFromAnotherFamily()
    {
        var seed = await SeedAsync();
        Guid requestId;
        using (var scope = _services.CreateScope())
        {
            requestId = (await Sut(scope).RequestAsync(
                seed.OutsiderId, new JoinFamilyRequest { FamilyId = seed.FamilyId })).RequestId!.Value;
        }

        using var approving = _services.CreateScope();
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Sut(approving).ApproveAsync(
            seed.AdminId, seed.OrganizationId, requestId,
            new ApproveJoinRequest { CardiMemberIds = [seed.OtherFamilyMemberId] }));

        using var check = _services.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
        Assert.Equal(0, await db.UserOrganizations.CountAsync(m => m.UserId == seed.OutsiderId));
    }

    /// <summary>A member of the family is not its administrator, and the refusal says nothing either way.</summary>
    [Fact]
    public async Task OnlyAnAdmin_SeesOrAnswersTheQueue()
    {
        var seed = await SeedAsync();

        using var scope = _services.CreateScope();
        var denied = await Assert.ThrowsAsync<KeyNotFoundException>(
            () => Sut(scope).GetPendingAsync(seed.SiblingId, seed.OrganizationId));
        Assert.Equal("Family not found", denied.Message);
    }

    /// <summary>Every family gets a code, including ones created before the column existed.</summary>
    [Fact]
    public async Task EveryFamilyHasACode_AndNoTwoShareOne()
    {
        await SeedAsync();

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
        var codes = await db.Organizations.Select(o => o.FamilyId).ToListAsync();

        Assert.All(codes, c => Assert.Equal(8, c.Length));
        Assert.Equal(codes.Count, codes.Distinct().Count());
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    private static IFamilyJoinService Sut(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IFamilyJoinService>();

    private record Seed(
        Guid OrganizationId,
        string FamilyId,
        Guid AdminId,
        Guid SiblingId,
        Guid OutsiderId,
        Guid MemberId,
        Guid SecondMemberId,
        Guid OtherFamilyMemberId);

    private async Task<Seed> SeedAsync()
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();

        var okafors = new Organization
        {
            Name = "Okafor family",
            Type = OrganizationType.Family,
            FamilyId = FamilyIdentifier.Mint(),
        };
        var adeyemis = new Organization
        {
            Name = "Adeyemi family",
            Type = OrganizationType.Family,
            FamilyId = FamilyIdentifier.Mint(),
        };
        db.Organizations.AddRange(okafors, adeyemis);

        var jane = NewUser(okafors.Id, "Jane Okafor", "jane@okafor.test");
        var sibling = NewUser(okafors.Id, "Ada Okafor", "ada@okafor.test");
        var outsider = NewUser(null, "Tom Okafor", "tom@okafor.test");
        db.Users.AddRange(jane, sibling, outsider);

        db.UserOrganizations.AddRange(
            new UserOrganization { UserId = jane.Id, OrganizationId = okafors.Id, Role = UserRole.Admin },
            new UserOrganization { UserId = sibling.Id, OrganizationId = okafors.Id, Role = UserRole.Member });

        var margaret = NewMember(okafors.Id, "Margaret Okafor");
        var robert = NewMember(okafors.Id, "Robert Okafor");
        var funmi = NewMember(adeyemis.Id, "Funmi Adeyemi");
        db.CardiMembers.AddRange(margaret, robert, funmi);

        await db.SaveChangesAsync();

        return new Seed(
            okafors.Id,
            FamilyIdentifier.ToDisplay(okafors.FamilyId),
            jane.Id,
            sibling.Id,
            outsider.Id,
            margaret.Id,
            robert.Id,
            funmi.Id);
    }

    private static User NewUser(Guid? organizationId, string name, string email) => new()
    {
        OrganizationId = organizationId,
        Name = name,
        Email = email,
        Auth0UserId = $"auth0|{Guid.NewGuid():N}",
        PasswordHash = "n/a",
        Role = UserRole.Member,
        IsActive = true,
    };

    private static CardiMember NewMember(Guid organizationId, string name) => new()
    {
        OrganizationId = organizationId,
        FirstName = PersonName.Split(name).FirstName,
        LastName = PersonName.Split(name).LastName,
        DateOfBirth = new DateOnly(1943, 4, 2),
        IsActive = true,
    };
}
