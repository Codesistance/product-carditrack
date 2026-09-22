using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Core.Family;

namespace CardiTrack.UnitTests.Mobile;

public class FamilyTabStateTests
{
    private static FamilySummary Family(string name, string role, bool home = false) =>
        new(Guid.NewGuid(), name, role, home, 1, []);

    [Fact]
    public void NoFamilies_IsTheNoFamilyState()
    {
        var selection = FamilyTabState.Resolve([], null);

        Assert.Equal(FamilyTabMode.NoFamily, selection.Mode);
        Assert.Null(selection.Family);
    }

    [Fact]
    public void TheDrawersChoiceWins_WhenItIsStillOneOfTheirFamilies()
    {
        var home = Family("Home", "admin", home: true);
        var other = Family("Other", "member");

        var selection = FamilyTabState.Resolve([home, other], other.OrganizationId);

        Assert.Same(other, selection.Family);
        Assert.Equal(FamilyTabMode.Member, selection.Mode);
    }

    [Fact]
    public void AStaleChoiceFallsBackToTheHomeFamily()
    {
        var first = Family("First", "member");
        var home = Family("Home", "admin", home: true);

        var selection = FamilyTabState.Resolve([first, home], Guid.NewGuid());

        Assert.Same(home, selection.Family);
        Assert.Equal(FamilyTabMode.Admin, selection.Mode);
    }

    [Fact]
    public void AGuestWithNoHomeFamilyGetsTheFirstOne()
    {
        var first = Family("First", "member");
        var second = Family("Second", "member");

        var selection = FamilyTabState.Resolve([first, second], null);

        Assert.Same(first, selection.Family);
    }

    [Theory]
    [InlineData("admin", true)]
    [InlineData("Admin ", true)]
    [InlineData("member", false)]
    [InlineData(null, false)]
    public void IsAdmin_ReadsTheWireRole(string? role, bool expected)
    {
        Assert.Equal(expected, FamilyTabState.IsAdmin(role));
    }
}
