using CardiTrack.Application.Services;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// The block of published figures the trend prompt carries. The brief forbids the model
/// converting a unit or recalling a benchmark, which makes this table the only yardstick it has —
/// so what these pin is that every band in it is one the features beside it can actually be read
/// against, in the units they are reported in, for the measurement they describe.
/// </summary>
public class PinnedReferenceTableTests
{
    [Theory]
    [InlineData(70, "7-8 hours", "420-480 minutes")]
    [InlineData(54, "7-9 hours", "420-540 minutes")]
    public void TheSleepBandIsGivenInTheUnitsTheSleepFeatureReports(
        int ageYears, string hours, string minutes)
    {
        // TrendFeatureCalculator reports sleep as "minutes a night". A band in hours alone left
        // the model an average of 412 minutes beside a recommendation of 7-9, with the brief
        // forbidding it from bridging the two — so it is given both ways, as the activity line
        // below it already was.
        var table = PinnedReferenceTable.For(ageYears);

        Assert.Contains(hours, table);
        Assert.Contains(minutes, table);

        // Named as the same recommendation, so two figures cannot read as two of them.
        Assert.Contains("not a second one", table);
    }

    [Fact]
    public void TheMinutesAreTheHoursAndNothingElse()
    {
        // The conversion is the whole point of the line; an arithmetic slip here would hand the
        // model a wrong figure it is under instruction to treat as authoritative.
        var band = HealthReferenceRanges.Sleep(70);

        Assert.Equal(7m, band.Low);
        Assert.Equal(8m, band.High);
        Assert.Contains($"{band.Low * 60:0}-{band.High * 60:0} minutes a night", PinnedReferenceTable.For(70));
    }

    /// <summary>
    /// The only breathing figure the trend features carry is measured asleep
    /// (<c>ActivityLog.OvernightBreathingRate</c>), and the adult band this codebase holds is
    /// WHO's rate at rest. Quoting it here handed the model a yardstick for a measurement it was
    /// not looking at, presented as the one figure it may treat as authoritative.
    /// </summary>
    [Fact]
    public void NoPublishedBandIsQuotedForBreathingAsleep()
    {
        var table = PinnedReferenceTable.For(70);

        Assert.DoesNotContain("Breathing at rest", table);
        Assert.DoesNotContain("12-20", table);
        Assert.Contains(HealthReferenceRanges.NoOvernightBreathingBand, table);

        // Said the same way HRV's absence is, since it is the same stance.
        Assert.Contains("own baseline", table);
    }

    [Fact]
    public void TheBandsThatDoApplyAreStillThere()
    {
        // The counterpart to the two removals above: this table exists so the model never recalls
        // a benchmark, and a table that lost its figures would send it back to its training data.
        var table = PinnedReferenceTable.For(70);

        Assert.Contains("60-100 bpm", table);
        Assert.Contains("94-100%", table);
        Assert.Contains("150-300 minutes a week", table);
        Assert.Contains(HealthReferenceRanges.NoHeartRateVariabilityBand, table);
    }

    [Fact]
    public void AChangeToTheFiguresCarriesANewVersion()
    {
        // Version 1 was the table with the hours-only sleep band and WHO's resting rate on the
        // overnight row. A stored narrative written against either has to be datable to it, which
        // is the whole reason the stamp exists.
        Assert.Equal(2, PinnedReferenceTable.Version);
        Assert.Equal("2", PinnedReferenceTable.VersionLabel);
        Assert.Contains("(version 2)", PinnedReferenceTable.For(70));
    }
}
