using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Core.Family;

namespace CardiTrack.UnitTests.Mobile;

public class FamilyAlertStateTests
{
    private static readonly Guid Margaret = Guid.NewGuid();
    private static readonly Guid Frank = Guid.NewGuid();
    private static readonly Guid Stranger = Guid.NewGuid();

    private static FamilySummary Family(string name) => new(Guid.NewGuid(), name, "member", false, 2, []);

    private static AlertSummaryResponse Alert(Guid member, string severity, int minutesAgo = 5) => new()
    {
        AlertId = Guid.NewGuid(),
        CardiMemberId = member,
        CardiMemberName = "Margaret Doe",
        Severity = severity,
        Status = "new",
        TriggeredAt = DateTime.UtcNow.AddMinutes(-minutesAgo),
    };

    [Fact]
    public void QuietWhenNoneOfTheFamilysMembersHaveOpenAlerts()
    {
        var state = FamilyAlertState.For([Margaret], [Alert(Stranger, "red")]);

        Assert.True(state.IsQuiet);
        Assert.Equal(0, state.OpenCount);
        Assert.Null(state.HighestSeverity);
    }

    [Fact]
    public void CountsMatchingAlerts_AndKeepsTheWorstSeverity()
    {
        var state = FamilyAlertState.For(
            [Margaret, Frank],
            [Alert(Margaret, "yellow", 30), Alert(Frank, "orange", 2), Alert(Margaret, "red", 90)]);

        Assert.Equal(3, state.OpenCount);
        Assert.Equal("red", state.HighestSeverity);
        // The most recent one, not the worst one, dates the family's row.
        Assert.True(state.MostRecentAt > DateTime.UtcNow.AddMinutes(-3));
    }

    [Fact]
    public void TwoFamiliesWatchingOnePersonUnderOneName_EachSeeOnlyTheirOwnRecord()
    {
        // D-15: two records, one name. The join is on the id, so the name is irrelevant.
        var theirs = Guid.NewGuid();
        var state = FamilyAlertState.For([Margaret], [Alert(theirs, "red")]);

        Assert.True(state.IsQuiet);
    }

    [Fact]
    public void FamilyWithNoMembersIsQuietWhateverTheAlerts()
    {
        Assert.True(FamilyAlertState.For([], [Alert(Margaret, "red")]).IsQuiet);
    }

    [Fact]
    public void MembersOf_PicksTheFamilysOwnFromTheGrantScopedList()
    {
        var ours = Guid.NewGuid();
        var members = new List<CardiMemberResponse>
        {
            new() { Id = Margaret, Name = "Margaret Doe", OrganizationId = ours },
            new() { Id = Frank, Name = "Frank Doe", OrganizationId = ours },
            new() { Id = Stranger, Name = "Margaret Doe", OrganizationId = Guid.NewGuid() },
        };

        var ids = FamilyAlertState.MembersOf(ours, members);

        Assert.Equal(2, ids.Count);
        Assert.Contains(Margaret, ids);
        Assert.DoesNotContain(Stranger, ids);
    }

    [Fact]
    public void OrderForDrawer_PutsFamiliesWithAlertsFirst_WorstFirst_ThenKeepsServerOrder()
    {
        var quietHome = Family("Home");
        var orange = Family("Orange");
        var quietSecond = Family("Second");
        var red = Family("Red");
        var states = new Dictionary<Guid, FamilyAlertSummary>
        {
            [quietHome.OrganizationId] = FamilyAlertSummary.Quiet,
            [orange.OrganizationId] = new(1, "orange", DateTime.UtcNow),
            [quietSecond.OrganizationId] = FamilyAlertSummary.Quiet,
            [red.OrganizationId] = new(1, "red", DateTime.UtcNow),
        };

        var ordered = FamilyAlertState.OrderForDrawer(
            [quietHome, orange, quietSecond, red], f => states[f.OrganizationId]);

        Assert.Equal(["Red", "Orange", "Home", "Second"], ordered.Select(f => f.Name));
    }

    [Theory]
    [InlineData("red", "orange", "red")]
    [InlineData("orange", "red", "red")]
    [InlineData(null, "yellow", "yellow")]
    [InlineData("yellow", null, "yellow")]
    [InlineData("green", "yellow", "yellow")]
    [InlineData(null, null, null)]
    public void HigherOf_FollowsTheSeverityContract(string? a, string? b, string? expected)
    {
        Assert.Equal(expected, FamilyAlertState.HigherOf(a, b));
    }
}
