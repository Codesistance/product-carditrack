using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Core.Family;

namespace CardiTrack.UnitTests.Mobile;

public class FamilyAlertStateTests
{
    private static FamilySummary Family(string name, params string[] watched) =>
        new(Guid.NewGuid(), name, "member", false, 2, watched);

    private static AlertSummaryResponse Alert(string member, string severity, int minutesAgo = 5) => new()
    {
        AlertId = Guid.NewGuid(),
        CardiMemberId = Guid.NewGuid(),
        CardiMemberName = member,
        Severity = severity,
        Status = "new",
        TriggeredAt = DateTime.UtcNow.AddMinutes(-minutesAgo),
    };

    [Fact]
    public void QuietWhenNoneOfTheFamilysMembersHaveOpenAlerts()
    {
        var family = Family("Doe", "Margaret Doe");

        var state = FamilyAlertState.For(family, [Alert("Somebody Else", "red")]);

        Assert.True(state.IsQuiet);
        Assert.Equal(0, state.OpenCount);
        Assert.Null(state.HighestSeverity);
    }

    [Fact]
    public void CountsMatchingAlerts_AndKeepsTheWorstSeverity()
    {
        var family = Family("Doe", "Margaret Doe", "Frank Doe");

        var state = FamilyAlertState.For(family,
        [
            Alert("Margaret Doe", "yellow", minutesAgo: 30),
            Alert("frank doe ", "orange", minutesAgo: 2),
            Alert("Margaret Doe", "red", minutesAgo: 90),
        ]);

        Assert.Equal(3, state.OpenCount);
        Assert.Equal("red", state.HighestSeverity);
        // The most recent one, not the worst one, dates the family's row.
        Assert.True(state.MostRecentAt > DateTime.UtcNow.AddMinutes(-3));
    }

    [Fact]
    public void MatchesNamesCaseInsensitively_AndIgnoresSurroundingSpace()
    {
        var family = Family("Doe", "  margaret DOE ");

        var state = FamilyAlertState.For(family, [Alert("Margaret Doe", "orange")]);

        Assert.Equal(1, state.OpenCount);
    }

    [Fact]
    public void FamilyWithNoWatchedMembersIsQuietWhateverTheAlerts()
    {
        var family = Family("Doe");

        var state = FamilyAlertState.For(family, [Alert("", "red"), Alert("Margaret Doe", "red")]);

        Assert.True(state.IsQuiet);
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
