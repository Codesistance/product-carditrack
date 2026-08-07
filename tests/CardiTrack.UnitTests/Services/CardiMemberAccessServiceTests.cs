using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using NSubstitute;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// The single gate every health-data surface goes through. These cases are the contract the
/// controllers and services above it rely on, so they are asserted here once rather than
/// re-tested on each caller.
/// </summary>
public class CardiMemberAccessServiceTests
{
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IUserCardiMemberRepository _links = Substitute.For<IUserCardiMemberRepository>();

    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _memberId = Guid.NewGuid();
    private readonly Guid _foreignMemberId = Guid.NewGuid();

    public CardiMemberAccessServiceTests()
    {
        _unitOfWork.UserCardiMembers.Returns(_links);
        SetupLinks(Link(_memberId));
    }

    private CardiMemberAccessService CreateSut() => new(_unitOfWork);

    private static UserCardiMember Link(
        Guid memberId, bool isActive = true, bool canViewHealthData = true) => new()
    {
        CardiMemberId = memberId,
        IsActive = isActive,
        CanViewHealthData = canViewHealthData,
    };

    private void SetupLinks(params UserCardiMember[] links)
    {
        foreach (var link in links)
            link.UserId = _userId;
        _links.GetByUserIdAsync(_userId).Returns(links);
    }

    // ── HasViewAccessAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task HasViewAccess_True_ForActiveLinkWithHealthDataPermission()
    {
        Assert.True(await CreateSut().HasViewAccessAsync(_userId, _memberId));
    }

    [Fact]
    public async Task HasViewAccess_False_ForMemberTheUserIsNotLinkedTo()
    {
        Assert.False(await CreateSut().HasViewAccessAsync(_userId, _foreignMemberId));
    }

    [Fact]
    public async Task HasViewAccess_False_WhenLinkIsInactive()
    {
        SetupLinks(Link(_memberId, isActive: false));

        Assert.False(await CreateSut().HasViewAccessAsync(_userId, _memberId));
    }

    [Fact]
    public async Task HasViewAccess_False_WhenLinkForbidsHealthData()
    {
        SetupLinks(Link(_memberId, canViewHealthData: false));

        Assert.False(await CreateSut().HasViewAccessAsync(_userId, _memberId));
    }

    [Fact]
    public async Task HasViewAccess_False_ForEmptyUserId_WithoutQueryingLinks()
    {
        Assert.False(await CreateSut().HasViewAccessAsync(Guid.Empty, _memberId));

        // An unset user id is a broken caller, not a user with no links — never let it reach a lookup
        // that some future data state could satisfy.
        await _links.DidNotReceive().GetByUserIdAsync(Arg.Any<Guid>());
    }

    // ── RequireViewAccessAsync (single) ─────────────────────────────────────────

    [Fact]
    public async Task RequireViewAccess_Passes_ForLinkedMember()
    {
        await CreateSut().RequireViewAccessAsync(_userId, _memberId);
    }

    [Fact]
    public async Task RequireViewAccess_Throws_ForForeignMember()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            CreateSut().RequireViewAccessAsync(_userId, _foreignMemberId));
    }

    [Fact]
    public async Task RequireViewAccess_DeniedMessage_DoesNotRevealWhetherTheMemberExists()
    {
        var linked = await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            CreateSut().RequireViewAccessAsync(_userId, Guid.NewGuid()));

        SetupLinks(Link(_memberId, canViewHealthData: false));
        var unpermitted = await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            CreateSut().RequireViewAccessAsync(_userId, _memberId));

        // A real-but-forbidden member and a nonexistent one must be indistinguishable to the caller.
        Assert.Equal(linked.Message, unpermitted.Message);
    }

    // ── RequireViewAccessAsync (set) ────────────────────────────────────────────

    [Fact]
    public async Task RequireViewAccess_Passes_WhenEveryMemberIsPermitted()
    {
        var second = Guid.NewGuid();
        SetupLinks(Link(_memberId), Link(second));

        await CreateSut().RequireViewAccessAsync(_userId, new[] { _memberId, second });
    }

    [Fact]
    public async Task RequireViewAccess_Throws_WhenAnyMemberIsNotPermitted()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            CreateSut().RequireViewAccessAsync(_userId, new[] { _memberId, _foreignMemberId }));
    }

    [Fact]
    public async Task RequireViewAccess_ChecksTheWholeSet_InOneRepositoryCall()
    {
        var second = Guid.NewGuid();
        SetupLinks(Link(_memberId), Link(second));

        await CreateSut().RequireViewAccessAsync(_userId, new[] { _memberId, second, _memberId });

        await _links.Received(1).GetByUserIdAsync(_userId);
    }

    [Fact]
    public async Task RequireViewAccess_Passes_ForEmptySet()
    {
        // Nothing was asked for, so nothing is disclosed; an empty request is the caller's
        // validation problem, not an access failure.
        await CreateSut().RequireViewAccessAsync(_userId, Array.Empty<Guid>());
    }

    // ── RequireManageAccessAsync ────────────────────────────────────────────────

    [Fact]
    public async Task RequireManageAccess_Passes_ForPrimaryCaregiver()
    {
        var link = Link(_memberId);
        link.IsPrimaryCaregiver = true;
        SetupLinks(link);

        await CreateSut().RequireManageAccessAsync(_userId, _memberId);
    }

    [Fact]
    public async Task RequireManageAccess_Throws_ForViewOnlyCaregiver()
    {
        // A relative invited to watch over someone may read their data but must not be able
        // to silence monitoring or delete them.
        var link = Link(_memberId);
        link.IsPrimaryCaregiver = false;
        SetupLinks(link);

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => CreateSut().RequireManageAccessAsync(_userId, _memberId));
    }

    [Fact]
    public async Task RequireManageAccess_Throws_ForInactiveLink()
    {
        var link = Link(_memberId, isActive: false);
        link.IsPrimaryCaregiver = true;
        SetupLinks(link);

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => CreateSut().RequireManageAccessAsync(_userId, _memberId));
    }

    [Fact]
    public async Task RequireManageAccess_Throws_ForMemberTheUserIsNotLinkedTo()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => CreateSut().RequireManageAccessAsync(_userId, _foreignMemberId));
    }

    [Fact]
    public async Task RequireManageAccess_Throws_ForEmptyUserId()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => CreateSut().RequireManageAccessAsync(Guid.Empty, _memberId));
    }

    [Fact]
    public async Task RequireManageAccess_ReportsDenialAsNotFound()
    {
        // Same message as a genuinely missing member — a distinct one would confirm the
        // member exists.
        var ex = await Assert.ThrowsAsync<KeyNotFoundException>(
            () => CreateSut().RequireManageAccessAsync(_userId, _foreignMemberId));

        Assert.Equal("CardiMember not found", ex.Message);
    }
}
