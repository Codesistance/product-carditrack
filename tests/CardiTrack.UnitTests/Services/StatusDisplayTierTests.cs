using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;

namespace CardiTrack.UnitTests.Services;

public class StatusDisplayTierTests
{
    private static readonly DateTime Now = new(2026, 9, 6, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void UnresolvedAlertsAlone_KeepTheirTier() =>
        Assert.Equal(AlertSeverity.Orange, StatusDisplayTier.Resolve(
            AlertSeverity.Orange, latestAssessment: null, latestFamilyDigest: null, Now));

    [Fact]
    public void AFreshYellowAssessment_RaisesGreenToYellow()
    {
        var assessment = new RealtimeAssessment
        {
            Severity = AlertSeverity.Yellow,
            WindowStartUtc = Now.AddHours(-2),
        };

        Assert.Equal(AlertSeverity.Yellow, StatusDisplayTier.Resolve(
            AlertSeverity.Green, assessment, latestFamilyDigest: null, Now));
    }

    [Fact]
    public void AStaleYellowAssessment_DoesNotRaise()
    {
        var assessment = new RealtimeAssessment
        {
            Severity = AlertSeverity.Yellow,
            WindowStartUtc = Now - StatusDisplayTier.AssessmentFreshness - TimeSpan.FromMinutes(1),
        };

        Assert.Equal(AlertSeverity.Green, StatusDisplayTier.Resolve(
            AlertSeverity.Green, assessment, latestFamilyDigest: null, Now));
    }

    [Fact]
    public void AGreenAssessment_DoesNotRaise()
    {
        var assessment = new RealtimeAssessment
        {
            Severity = AlertSeverity.Green,
            WindowStartUtc = Now.AddHours(-1),
        };

        Assert.Equal(AlertSeverity.Green, StatusDisplayTier.Resolve(
            AlertSeverity.Green, assessment, latestFamilyDigest: null, Now));
    }

    [Fact]
    public void TodaysCheckInDigest_RaisesGreenToYellow()
    {
        var digest = new DigestEntry { Urgency = DigestUrgency.CheckIn };

        Assert.Equal(AlertSeverity.Yellow, StatusDisplayTier.Resolve(
            AlertSeverity.Green, latestAssessment: null, digest, Now));
    }

    [Fact]
    public void AWatchDigest_DoesNotRaise()
    {
        var digest = new DigestEntry { Urgency = DigestUrgency.Watch };

        Assert.Equal(AlertSeverity.Green, StatusDisplayTier.Resolve(
            AlertSeverity.Green, latestAssessment: null, digest, Now));
    }

    [Fact]
    public void TheWorstSignalWins()
    {
        var assessment = new RealtimeAssessment
        {
            Severity = AlertSeverity.Yellow,
            WindowStartUtc = Now.AddHours(-1),
        };
        var digest = new DigestEntry { Urgency = DigestUrgency.ActNow };

        Assert.Equal(AlertSeverity.Red, StatusDisplayTier.Resolve(
            AlertSeverity.Orange, assessment, digest, Now));
    }
}
