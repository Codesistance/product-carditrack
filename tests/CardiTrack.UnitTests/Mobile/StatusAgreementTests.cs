using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Core.Alerts;

namespace CardiTrack.UnitTests.Mobile;

/// <summary>
/// The line that reconciles the summary card with the dashboard hero on the day they disagree.
/// Worth pinning both ways: shown when it should not be, it tells a family an alert is open that
/// is not; missing when it should show, it leaves them with a yellow dashboard and a green summary
/// and no way to tell which to believe.
/// </summary>
public class StatusAgreementTests
{
    private static CardiMemberDetailResponse Member(string healthStatus, bool paused = false) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Dad",
        HealthStatus = healthStatus,
        MonitoringPaused = paused,
    };

    private static DigestResponse Digest(string? urgency) => new()
    {
        CardiMemberId = Guid.NewGuid(),
        LocalDate = new DateOnly(2026, 9, 7),
        Audience = "Family",
        Text = "A quiet day, with heart rate slightly above usual.",
        Urgency = urgency,
        GeneratedAtUtc = DateTime.UtcNow,
    };

    // The case seen on the emulator: a yellow hero over "Nothing pressing today".
    [Fact]
    public void AYellowHeroOverAWatchRung_NamesTheOpenAlert_AndTheColour()
    {
        var note = StatusAgreement.Note(Member("yellow"), Digest("watch"));

        Assert.NotNull(note);
        Assert.Contains("yellow", note);
        Assert.Contains("alert is still open", note);
        // Where to go next — the alert may be acknowledged and so already off the dashboard.
        Assert.Contains("Alerts", note);
    }

    [Theory]
    [InlineData("orange", "watch")]
    [InlineData("orange", "check-in")]
    [InlineData("red", "concerning")]
    [InlineData("red", "watch")]
    public void AnyHeroAboveTheRung_Explains(string healthStatus, string urgency)
    {
        Assert.NotNull(StatusAgreement.Note(Member(healthStatus), Digest(urgency)));
    }

    [Theory]
    [InlineData("green", "watch")]
    [InlineData("yellow", "check-in")]
    [InlineData("orange", "concerning")]
    [InlineData("red", "act-now")]
    public void AgreeingTiers_SayNothing(string healthStatus, string urgency)
    {
        Assert.Null(StatusAgreement.Note(Member(healthStatus), Digest(urgency)));
    }

    // The digest asking for more than the alerts do is the other disagreement, and not this
    // note's: there is no open alert to point at, and the hero's own sentence already takes
    // the digest's tier.
    [Theory]
    [InlineData("green", "check-in")]
    [InlineData("yellow", "act-now")]
    public void ARungAboveTheHero_SaysNothing(string healthStatus, string urgency)
    {
        Assert.Null(StatusAgreement.Note(Member(healthStatus), Digest(urgency)));
    }

    // No baseline yet: the hero shows the day's own readings, not a colour to be above anything.
    [Fact]
    public void AnUnknownHero_SaysNothing()
    {
        Assert.Null(StatusAgreement.Note(Member("unknown"), Digest("watch")));
    }

    // HealthStatus is still computed for a paused member, but their hero says "Monitoring
    // paused" — claiming an alert is colouring it would be describing a card that is not there.
    [Fact]
    public void APausedMember_SaysNothing_EvenWithAYellowStatus()
    {
        Assert.Null(StatusAgreement.Note(Member("yellow", paused: true), Digest("watch")));
    }

    // A generation that returned nothing parseable shows no rung, so there is nothing to sit under.
    [Fact]
    public void NoUrgency_SaysNothing()
    {
        Assert.Null(StatusAgreement.Note(Member("yellow"), Digest(null)));
    }

    [Theory]
    [InlineData("amber", "watch")]
    [InlineData("yellow", "urgent")]
    public void AWordThisClientDoesNotKnow_SaysNothing(string healthStatus, string urgency)
    {
        Assert.Null(StatusAgreement.Note(Member(healthStatus), Digest(urgency)));
    }

    [Fact]
    public void ReadsAsAPerson_NotADatabaseRow()
    {
        var note = StatusAgreement.Note(Member("yellow"), Digest("watch"))!;

        Assert.DoesNotContain("CardiMember", note);
        Assert.DoesNotContain("unresolved", note);
        Assert.DoesNotContain("severity", note);
    }
}
