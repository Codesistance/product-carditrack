using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Core.Onboarding;

namespace CardiTrack.UnitTests.Mobile;

public class PrimaryCardiMemberTests
{
    private static CardiMemberResponse Member(string name, bool isActive = true) =>
        new() { Id = Guid.NewGuid(), Name = name, IsActive = isActive };

    [Fact]
    public void ReturnsNull_WhenThereAreNoMembers()
    {
        Assert.Null(PrimaryCardiMember.From([]));
        Assert.Null(PrimaryCardiMember.From(null));
    }

    [Fact]
    public void PrefersTheFirstActiveMember()
    {
        var active = Member("Margaret");
        var members = new List<CardiMemberResponse> { Member("Archived", isActive: false), active };

        Assert.Same(active, PrimaryCardiMember.From(members));
    }

    [Fact]
    public void FallsBackToTheFirstMember_WhenNoneAreActive()
    {
        var first = Member("Margaret", isActive: false);
        var members = new List<CardiMemberResponse> { first, Member("Arthur", isActive: false) };

        Assert.Same(first, PrimaryCardiMember.From(members));
    }

    [Fact]
    public void HonoursTheRememberedMember_EvenWhenItIsNotFirstOrActive()
    {
        var remembered = Member("Arthur", isActive: false);
        var members = new List<CardiMemberResponse> { Member("Margaret"), remembered };

        Assert.Same(remembered, PrimaryCardiMember.From(members, remembered.Id));
    }

    [Fact]
    public void IgnoresARememberedMemberThatIsGone()
    {
        // Deleted on another device, or belonging to a previous session.
        var active = Member("Margaret");
        var members = new List<CardiMemberResponse> { active };

        Assert.Same(active, PrimaryCardiMember.From(members, Guid.NewGuid()));
    }

    [Fact]
    public void ReturnsNull_WhenARememberedMemberIsGoneAndTheListIsEmpty()
    {
        Assert.Null(PrimaryCardiMember.From([], Guid.NewGuid()));
    }
}
