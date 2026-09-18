using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Security;
using CardiTrack.UnitTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CardiTrack.UnitTests.Repositories;

/// <summary>
/// Against the real PostgreSQL container, because every claim this repository makes is a claim about
/// SQL. The one-live-invite rule is a partial unique index, not a check the service performs; the
/// status transitions are conditional UPDATEs whose whole point is that two racing callers cannot
/// both win; and the sweep's two-way date predicate has to translate. A substitute could vouch for
/// none of it, and a query that fails to translate fails only at runtime.
/// </summary>
[Collection("DatabaseCollection")]
public class DeviceConnectionInviteRepositoryTests(TestDatabaseFixture fixture)
{
    private static readonly DateTime Now = new(2026, 9, 18, 9, 0, 0, DateTimeKind.Utc);

    private static DeviceConnectionInvite Invite(
        Guid memberId,
        DeviceInviteStatus status = DeviceInviteStatus.Pending,
        DeviceType deviceType = DeviceType.Fitbit,
        DateTime? expiresAt = null,
        DateTime? resolvedAt = null) => new()
        {
            Id = Guid.NewGuid(),
            CardiMemberId = memberId,
            CreatedByUserId = Guid.NewGuid(),
            DeviceType = deviceType,
            Channel = DeviceInviteChannel.Link,
            TokenHash = InviteTokens.HashOrNull(InviteTokens.Mint())!,
            Status = status,
            ExpiresAt = expiresAt ?? Now.AddHours(24),
            ResolvedAt = resolvedAt,
        };

    private static async Task<IDeviceConnectionInviteRepository> SaveAsync(
        IServiceScope scope, params DeviceConnectionInvite[] invites)
    {
        var repo = scope.ServiceProvider.GetRequiredService<IDeviceConnectionInviteRepository>();
        foreach (var invite in invites)
            await repo.AddAsync(invite);

        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        return repo;
    }

    [Fact]
    public async Task ASecondLiveInviteForTheSameMemberAndBrand_IsRefusedByTheDatabase()
    {
        using var scope = fixture.CreateScope();
        var org = await TestDataSeeder.SeedOrganizationAsync(scope);
        var member = await TestDataSeeder.SeedCardiMemberAsync(scope, org.Id);

        await SaveAsync(scope, Invite(member.Id));

        // The service revokes the old invite before inserting a new one, but a caregiver
        // double-tapping — or two caregivers in the same care circle tapping at once — is a race
        // that check cannot close. The index can, and the loser must fail rather than leave the
        // wearer holding two live links to the same consent.
        await Assert.ThrowsAsync<DbUpdateException>(
            () => SaveAsync(scope, Invite(member.Id)));
    }

    [Theory]
    [InlineData(DeviceInviteStatus.Completed)]
    [InlineData(DeviceInviteStatus.Declined)]
    [InlineData(DeviceInviteStatus.Revoked)]
    public async Task AFinishedInvite_DoesNotBlockTheNextOne(DeviceInviteStatus finished)
    {
        using var scope = fixture.CreateScope();
        var org = await TestDataSeeder.SeedOrganizationAsync(scope);
        var member = await TestDataSeeder.SeedCardiMemberAsync(scope, org.Id);

        await SaveAsync(scope, Invite(member.Id, finished, resolvedAt: Now));

        // The index is filtered to the live statuses precisely so a history of finished invitations
        // never stops somebody sending another.
        await SaveAsync(scope, Invite(member.Id));
    }

    [Fact]
    public async Task ADifferentBrand_GetsItsOwnLiveInvite()
    {
        using var scope = fixture.CreateScope();
        var org = await TestDataSeeder.SeedOrganizationAsync(scope);
        var member = await TestDataSeeder.SeedCardiMemberAsync(scope, org.Id);

        await SaveAsync(scope, Invite(member.Id, deviceType: DeviceType.Fitbit));

        // A wearer can be asked to connect a watch and a scale; the rule is one per brand, not one
        // per person.
        await SaveAsync(scope, Invite(member.Id, deviceType: DeviceType.Withings));
    }

    [Fact]
    public async Task GetLive_IgnoresAnInviteWhoseDeadlineHasPassed()
    {
        using var scope = fixture.CreateScope();
        var org = await TestDataSeeder.SeedOrganizationAsync(scope);
        var member = await TestDataSeeder.SeedCardiMemberAsync(scope, org.Id);

        var repo = await SaveAsync(
            scope, Invite(member.Id, expiresAt: Now.AddMinutes(-1)));

        Assert.Null(await repo.GetLiveAsync(member.Id, DeviceType.Fitbit, Now));
    }

    [Fact]
    public async Task RevokeLive_ClearsAnExpiredRowToo_SoANewInviteCanBeCreated()
    {
        using var scope = fixture.CreateScope();
        var org = await TestDataSeeder.SeedOrganizationAsync(scope);
        var member = await TestDataSeeder.SeedCardiMemberAsync(scope, org.Id);

        var repo = await SaveAsync(scope, Invite(member.Id, expiresAt: Now.AddMinutes(-1)));

        // An expired invitation no longer works, but it still occupies the partial unique index
        // until something moves it out of a live status. If the sweep missed it, it must not be
        // what stops a caregiver asking for a new one.
        Assert.Equal(1, await repo.RevokeLiveAsync(member.Id, DeviceType.Fitbit, Now));
        await SaveAsync(scope, Invite(member.Id));
    }

    [Fact]
    public async Task TryResolve_LetsExactlyOneCallerWin()
    {
        using var scope = fixture.CreateScope();
        var org = await TestDataSeeder.SeedOrganizationAsync(scope);
        var member = await TestDataSeeder.SeedCardiMemberAsync(scope, org.Id);

        var invite = Invite(member.Id);
        var repo = await SaveAsync(scope, invite);

        DeviceInviteStatus[] live = [DeviceInviteStatus.Pending, DeviceInviteStatus.Opened];

        // The wearer completing while the caregiver cancels. A read-then-write would let both
        // decide the invite was open and the second would silently overwrite the first's outcome.
        Assert.True(await repo.TryResolveAsync(
            invite.Id, live, DeviceInviteStatus.Completed, Now, Guid.NewGuid()));
        Assert.False(await repo.TryResolveAsync(
            invite.Id, live, DeviceInviteStatus.Revoked, Now, null));

        var stored = await repo.GetByIdAsync(invite.Id);
        Assert.Equal(DeviceInviteStatus.Completed, stored!.Status);
    }

    [Fact]
    public async Task TryResolve_KeepsAnAlreadyRecordedConnection_WhenNoneIsSupplied()
    {
        using var scope = fixture.CreateScope();
        var org = await TestDataSeeder.SeedOrganizationAsync(scope);
        var member = await TestDataSeeder.SeedCardiMemberAsync(scope, org.Id);

        var deviceId = Guid.NewGuid();
        var invite = Invite(member.Id);
        var repo = await SaveAsync(scope, invite);

        await repo.TryResolveAsync(
            invite.Id, [DeviceInviteStatus.Pending], DeviceInviteStatus.Completed, Now, deviceId);

        var stored = await repo.GetByIdAsync(invite.Id);
        Assert.Equal(deviceId, stored!.DeviceConnectionId);
    }

    [Fact]
    public async Task TryMarkOpened_OnlyMovesAPendingInvite()
    {
        using var scope = fixture.CreateScope();
        var org = await TestDataSeeder.SeedOrganizationAsync(scope);
        var member = await TestDataSeeder.SeedCardiMemberAsync(scope, org.Id);

        var invite = Invite(member.Id);
        var repo = await SaveAsync(scope, invite);

        Assert.True(await repo.TryMarkOpenedAsync(invite.Id, Now));

        // A reload is not an error, and it must not keep moving the timestamp: "when did they first
        // look at this" is the question the caregiver's screen is really asking.
        Assert.False(await repo.TryMarkOpenedAsync(invite.Id, Now.AddMinutes(5)));

        var stored = await repo.GetByIdAsync(invite.Id);
        Assert.Equal(Now, stored!.OpenedAt);
    }

    [Fact]
    public async Task GetSweepable_TakesFinishedAndAbandonedInvitesAlike()
    {
        using var scope = fixture.CreateScope();
        var org = await TestDataSeeder.SeedOrganizationAsync(scope);
        var member = await TestDataSeeder.SeedCardiMemberAsync(scope, org.Id);
        var other = await TestDataSeeder.SeedCardiMemberAsync(scope, org.Id);

        var cutoff = Now.AddDays(-30);

        var resolvedLongAgo = Invite(
            member.Id, DeviceInviteStatus.Completed, resolvedAt: cutoff.AddDays(-1),
            expiresAt: cutoff.AddDays(-2));
        var expiredUntouched = Invite(
            other.Id, DeviceInviteStatus.Pending, expiresAt: cutoff.AddDays(-1));
        var resolvedRecently = Invite(
            member.Id, DeviceInviteStatus.Declined, deviceType: DeviceType.Withings,
            resolvedAt: Now.AddDays(-1), expiresAt: Now.AddDays(-2));

        var repo = await SaveAsync(scope, resolvedLongAgo, expiredUntouched, resolvedRecently);

        var sweepable = await repo.GetSweepableAsync(cutoff, 100);
        var ids = sweepable.Select(i => i.Id).ToList();

        // An invitation sent and ignored is retained no longer than one that was used, so both the
        // resolved and the merely-expired rows are aged from when they stopped mattering.
        Assert.Contains(resolvedLongAgo.Id, ids);
        Assert.Contains(expiredUntouched.Id, ids);
        Assert.DoesNotContain(resolvedRecently.Id, ids);
    }

    [Fact]
    public async Task GetByTokenHash_FindsOnlyAnExactMatch()
    {
        using var scope = fixture.CreateScope();
        var org = await TestDataSeeder.SeedOrganizationAsync(scope);
        var member = await TestDataSeeder.SeedCardiMemberAsync(scope, org.Id);

        var invite = Invite(member.Id);
        var repo = await SaveAsync(scope, invite);

        Assert.Equal(invite.Id, (await repo.GetByTokenHashAsync(invite.TokenHash))!.Id);

        // The column is fixed-length CHAR(64): a prefix must not match by padding, or a shortened
        // token would be as good as the real one.
        Assert.Null(await repo.GetByTokenHashAsync(invite.TokenHash[..32]));
    }
}
