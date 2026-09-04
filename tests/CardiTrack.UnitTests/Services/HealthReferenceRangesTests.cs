using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Services;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// Pins the band-position primitive the three sanctioned readers of a published range share, so a
/// boundary reading cannot mean one thing in an alert's copy and another in the digest beside it.
/// </summary>
public class HealthReferenceRangesTests
{
    private static readonly MetricReference Resting = HealthReferenceRanges.RestingHeartRate;

    [Theory]
    [InlineData(59, BandPosition.Below)]
    // Both bounds are inclusive: the range is quoted as "60 to 100", and a caregiver told 100 bpm
    // is past it would be reading a boundary artefact as a finding.
    [InlineData(60, BandPosition.Within)]
    [InlineData(100, BandPosition.Within)]
    [InlineData(101, BandPosition.Above)]
    public void Position_TreatsBothBoundsAsInclusive(int value, BandPosition expected) =>
        Assert.Equal(expected, HealthReferenceRanges.Position(Resting, value));

    /// <summary>
    /// "Nothing to compare" must never read as "compared and fine" — the callers branch on these
    /// two differently, and <see cref="BandPosition.Unknown"/> is what keeps them able to.
    /// </summary>
    [Fact]
    public void Position_IsUnknown_WithNothingToCompare()
    {
        Assert.Equal(BandPosition.Unknown, HealthReferenceRanges.Position(Resting, null));
        Assert.Equal(BandPosition.Unknown, HealthReferenceRanges.Position(null, 72));
    }

    [Fact]
    public void BandClause_StaysSilent_InsideTheBand()
    {
        Assert.Null(HealthReferenceRanges.BandClause(Resting, 72, "bpm"));
        Assert.Null(HealthReferenceRanges.BandClause(Resting, null, "bpm"));
    }

    [Fact]
    public void BandClause_NamesThePublisher_AndTheDirection()
    {
        Assert.Equal(
            "above the 60–100 bpm typical for an adult (AHA)",
            HealthReferenceRanges.BandClause(Resting, 118, "bpm"));
        Assert.Equal(
            "below the 60–100 bpm typical for an adult (AHA)",
            HealthReferenceRanges.BandClause(Resting, 44, "bpm"));
    }

    /// <summary>
    /// The variant for copy that exists because something already fired, where "inside the range"
    /// is the proportion the finding would otherwise leave a reader to guess at.
    /// </summary>
    [Fact]
    public void BandPlacement_Speaks_InsideTheBandToo()
    {
        Assert.Equal(
            "inside the 60–100 bpm typical for an adult (AHA)",
            HealthReferenceRanges.BandPlacement(Resting, 72, "bpm"));
        Assert.Null(HealthReferenceRanges.BandPlacement(Resting, null, "bpm"));
    }

    /// <summary>
    /// The sleep band is the one published range that moves with age, and the clause has to carry
    /// whichever ceiling applied — the same figures the rule stamps into its own MetricValues.
    /// </summary>
    [Fact]
    public void BandClause_CarriesTheAgeSplitSleepCeiling()
    {
        Assert.Equal(
            "above the 7–9 hours typical for an adult (NSF)",
            HealthReferenceRanges.BandClause(HealthReferenceRanges.Sleep(40), 9.5m, "hours"));
        Assert.Equal(
            "above the 7–8 hours typical for an adult (NSF)",
            HealthReferenceRanges.BandClause(
                HealthReferenceRanges.Sleep(HealthReferenceRanges.OlderAdultAge), 8.5m, "hours"));
    }
}
