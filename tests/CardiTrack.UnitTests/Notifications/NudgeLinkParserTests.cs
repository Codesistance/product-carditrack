using CardiTrack.Application.Services.Notifications;
using CardiTrack.Domain.Enums;
using CardiTrack.Mobile.Core.Notifications;
using Xunit;

namespace CardiTrack.UnitTests.Notifications;

/// <summary>
/// The join between a rule's action link and a screen. It breaks silently — a comply button that
/// goes nowhere still looks like a working button — so every link the catalogue can emit is
/// asserted to parse.
/// </summary>
public class NudgeLinkParserTests
{
    private static readonly Guid MemberId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Theory]
    [InlineData("carditrack://cardimembers/22222222-2222-2222-2222-222222222222/devices",
        NudgeDestinationKind.MemberDevices)]
    [InlineData("carditrack://cardimembers/22222222-2222-2222-2222-222222222222/devices/33333333-3333-3333-3333-333333333333",
        NudgeDestinationKind.MemberDevices)]
    [InlineData("carditrack://cardimembers/22222222-2222-2222-2222-222222222222/edit#medicalNotes",
        NudgeDestinationKind.MemberEdit)]
    [InlineData("carditrack://cardimembers/22222222-2222-2222-2222-222222222222/baseline",
        NudgeDestinationKind.MemberBaseline)]
    [InlineData("carditrack://cardimembers/22222222-2222-2222-2222-222222222222",
        NudgeDestinationKind.MemberDetail)]
    public void MemberScopedLinksCarryTheirMemberThrough(string link, NudgeDestinationKind expected)
    {
        var destination = NudgeLinkParser.Parse(link);

        Assert.Equal(expected, destination.Kind);
        Assert.Equal(MemberId, destination.CardiMemberId);
    }

    [Fact]
    public void TheTimeZoneFragmentIsItsOwnDestination()
    {
        // Distinguished from plain settings so the inbox can answer it from the device clock
        // instead of navigating to a screen and asking the user to find their own city.
        Assert.Equal(
            NudgeDestinationKind.TimeZone,
            NudgeLinkParser.Parse("carditrack://settings/profile#timezone").Kind);

        Assert.Equal(
            NudgeDestinationKind.Settings,
            NudgeLinkParser.Parse("carditrack://settings/profile").Kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://carditrack.com/cardimembers/x")]
    [InlineData("carditrack://")]
    [InlineData("carditrack://somewhere/else")]
    [InlineData("carditrack://cardimembers/not-a-guid/devices")]
    public void AnythingUnrecognisedParsesToUnknownRatherThanAGuess(string? link)
    {
        Assert.Equal(NudgeDestinationKind.Unknown, NudgeLinkParser.Parse(link).Kind);
    }

    [Fact]
    public void EveryLinkTheCatalogueCanEmitResolvesToAKnownDestination()
    {
        var contexts = new[]
        {
            new NudgeContextBuilder().NoConnections().Build(),
            new NudgeContextBuilder().AccountLevel().TimeZone("UTC").Build(),
            new NudgeContextBuilder().NoMedicalNotes().Build(),
            new NudgeContextBuilder().PausedUntil(NudgeContextBuilder.Now.AddDays(20)).Build(),
            new NudgeContextBuilder()
                .WithConnections(NudgeContextBuilder.Connection(ConnectionStatus.TokenExpired))
                .Build(),
            new NudgeContextBuilder()
                .WithConnections(NudgeContextBuilder.Connection(
                    lastSync: NudgeContextBuilder.Now.AddDays(-9), scopes: "activity_and_fitness"))
                .Build(),
            new NudgeContextBuilder()
                .NoBaseline().DaysCaptured(4)
                .LastActivity(DateOnly.FromDateTime(NudgeContextBuilder.Now).AddDays(-20))
                .Build()
        };

        foreach (var rule in NudgeRuleCatalogue.All)
        {
            foreach (var verdict in contexts.Select(rule.Evaluate).Where(v => v.HasGap))
            {
                var destination = NudgeLinkParser.Parse(verdict.ActionDeepLink);

                Assert.True(destination.Kind != NudgeDestinationKind.Unknown,
                    $"{rule.RuleCode} emits '{verdict.ActionDeepLink}', which no screen serves.");
            }
        }
    }
}
