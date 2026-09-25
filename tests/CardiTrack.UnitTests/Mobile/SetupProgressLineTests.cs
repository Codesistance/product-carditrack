using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Core.Notifications;

namespace CardiTrack.UnitTests.Mobile;

public class SetupProgressLineTests
{
    private static MemberSetupStep Step(string key, string title, bool done) =>
        new() { Key = key, Title = title, Done = done, ActionDeepLink = $"carditrack://{key}" };

    private static MemberSetupProgress Member(string name, bool isOwner, params MemberSetupStep[] steps) =>
        new()
        {
            CardiMemberId = Guid.NewGuid(),
            CardiMemberFirstName = name,
            IsOwner = isOwner,
            Done = steps.Count(s => s.Done),
            Total = steps.Length,
            Steps = [.. steps],
        };

    [Fact]
    public void AMemberWithStepsLeft_GetsTheirCountAndTheFirstUndoneStepAsNext()
    {
        var pop = Member("Pop", isOwner: true,
            Step("time-zone", "Time zone", true),
            Step("emergency-contact", "Emergency contact", false),
            Step("medical-information", "Medical information", false));

        var line = Assert.Single(SetupProgressLine.For([pop]));

        Assert.Equal("Pop's profile 1 of 3", line.Title);
        Assert.Equal("Next: emergency contact", line.Next);
        Assert.Equal("emergency-contact", line.NextStep.Key);
        Assert.Equal(1.0 / 3, line.Fraction, precision: 6);
    }

    [Fact]
    public void Finished_Empty_AndSomebodyElsesMembers_AreLeftOut()
    {
        var done = Member("Ann", true, Step("time-zone", "Time zone", true));
        var nothingApplies = Member("Bob", true);
        var relative = Member("Cal", false, Step("emergency-contact", "Emergency contact", false));

        Assert.Empty(SetupProgressLine.For([done, nothingApplies, relative]));
    }

    [Theory]
    [InlineData("James", "James' profile 0 of 1")]
    [InlineData("", "Their profile 0 of 1")]
    public void TheTitle_ReadsAsAPossessive(string name, string expected)
    {
        var member = Member(name, true, Step("time-zone", "Time zone", false));

        Assert.Equal(expected, SetupProgressLine.For([member])[0].Title);
    }

    [Fact]
    public void AnAcronymLedTitle_KeepsItsCase()
    {
        var member = Member("Pop", true, Step("irn", "IRN notifications", false));

        Assert.Equal("Next: IRN notifications", SetupProgressLine.For([member])[0].Next);
    }
}
