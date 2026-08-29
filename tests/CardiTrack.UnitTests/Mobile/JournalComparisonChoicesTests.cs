using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Domain.Common;
using CardiTrack.Mobile.Core.Journal;

namespace CardiTrack.UnitTests.Mobile;

/// <summary>
/// What the journal settings screen offers for the comparison tolerances, and what it calls each
/// value. The page itself is MAUI and cannot be constructed without a host; this is the part of
/// those rows worth pinning — which values are offered, and whether a tapped label maps back to
/// the number it came from.
/// </summary>
public class JournalComparisonChoicesTests
{
    private static JournalSettingsResponse Settings() => new()
    {
        EffectiveBedtimeToleranceMinutes = JournalComparison.DefaultBedtimeToleranceMinutes,
        EffectiveWakeToleranceMinutes = JournalComparison.DefaultWakeToleranceMinutes,
        EffectiveDirectionBoundMinutes = JournalComparison.DefaultDirectionBoundMinutes,
        EffectiveLevelTolerancePercent = JournalComparison.DefaultLevelTolerancePercent,
        SelectableToleranceMinutes = JournalComparison.SelectableToleranceMinutes,
        SelectableDirectionBoundMinutes = JournalComparison.SelectableDirectionBoundMinutes,
        SelectableLevelTolerancePercents = JournalComparison.SelectableLevelTolerancePercents,
    };

    // ── The ladder comes from the server ────────────────────────────────────────

    /// <summary>
    /// The offered rungs are the server's, not this build's. The ladder is published for the same
    /// reason the book timings' window and step are: an app that invents its own drifts from the
    /// server the day either changes.
    /// </summary>
    [Fact]
    public void ToleranceOptions_OfferTheLadderTheServerPublished()
    {
        var settings = Settings();
        settings.SelectableToleranceMinutes = [0, 25, 99];

        Assert.Equal(
            ["Every minute", "25 min", "99 min"],
            JournalComparisonChoices.ToleranceOptions(settings));
    }

    /// <summary>
    /// A response from before the field existed still opens onto something: a row whose picker is
    /// empty reads as broken, where one showing the defaults reads as unset.
    /// </summary>
    [Fact]
    public void ToleranceOptions_FallBackToTheCompiledLadder_WhenTheServerSentNone()
    {
        var settings = Settings();
        settings.SelectableToleranceMinutes = [];

        Assert.Equal(
            JournalComparison.SelectableToleranceMinutes.Count,
            JournalComparisonChoices.ToleranceOptions(settings).Count);
    }

    // ── What each value is called ───────────────────────────────────────────────

    /// <summary>
    /// Zero is the most talkative setting, not the absence of one, and the label says so — "None"
    /// would read as the comparison being switched off.
    /// </summary>
    [Fact]
    public void ToleranceLabel_CallsZero_EveryMinute()
    {
        Assert.Equal("Every minute", JournalComparisonChoices.ToleranceLabel(0));
        Assert.Equal("20 min", JournalComparisonChoices.ToleranceLabel(20));
    }

    /// <summary>
    /// The far bound is said in hours: "360 min" is a number a reader converts before it means
    /// anything about a night.
    /// </summary>
    [Theory]
    [InlineData(60, "1 hour")]
    [InlineData(90, "1h 30m")]
    [InlineData(360, "6 hours")]
    [InlineData(720, "12 hours")]
    public void DirectionBoundLabel_SaysHoursPastTheHourMark(int minutes, string expected)
    {
        Assert.Equal(expected, JournalComparisonChoices.DirectionBoundLabel(minutes));
    }

    /// <summary>
    /// The level band is offered in plain words with its percentage in brackets. A caregiver is
    /// choosing how much small movement gets mentioned, not reasoning about a share of a
    /// thirty-day mean — but the label stays honest about what it sets.
    /// </summary>
    [Fact]
    public void LevelToleranceLabel_LeadsWithTheEffect_AndKeepsThePercentInBrackets()
    {
        Assert.Equal("Mention every difference", JournalComparisonChoices.LevelToleranceLabel(0m));
        Assert.Equal("Ignore slight ones (1%)", JournalComparisonChoices.LevelToleranceLabel(1m));
        Assert.Equal("Ignore small ones (2%)", JournalComparisonChoices.LevelToleranceLabel(2m));
        Assert.Equal("Ignore anything under 5%", JournalComparisonChoices.LevelToleranceLabel(5m));
    }

    // ── Reading the tapped label back ───────────────────────────────────────────

    /// <summary>
    /// Every option this class offers maps back to the value it was built from. Matched on the
    /// label rather than on the sheet's index, which would break silently the day the ladder gains
    /// a rung — and the round trip is what stops a caregiver tapping "45 min" and saving 30.
    /// </summary>
    [Fact]
    public void EveryOfferedOption_RoundTripsBackToItsOwnValue()
    {
        var settings = Settings();

        foreach (var minutes in JournalComparison.SelectableToleranceMinutes)
        {
            var label = JournalComparisonChoices.ToleranceLabel(minutes);
            Assert.Equal(minutes, JournalComparisonChoices.ToleranceFor(settings, label));
        }

        foreach (var minutes in JournalComparison.SelectableDirectionBoundMinutes)
        {
            var label = JournalComparisonChoices.DirectionBoundLabel(minutes);
            Assert.Equal(minutes, JournalComparisonChoices.DirectionBoundFor(settings, label));
        }

        foreach (var percent in JournalComparison.SelectableLevelTolerancePercents)
        {
            var label = JournalComparisonChoices.LevelToleranceLabel(percent);
            Assert.Equal(percent, JournalComparisonChoices.LevelToleranceFor(settings, label));
        }
    }

    /// <summary>
    /// A cancelled sheet returns null, and null must not be read as a choice — the row is left
    /// exactly as it was rather than saving whatever the first rung happens to be.
    /// </summary>
    [Fact]
    public void ACancelledSheet_IsNotAChoice()
    {
        var settings = Settings();

        Assert.Null(JournalComparisonChoices.ToleranceFor(settings, null));
        Assert.Null(JournalComparisonChoices.ToleranceFor(settings, string.Empty));
        Assert.Null(JournalComparisonChoices.DirectionBoundFor(settings, null));
        Assert.Null(JournalComparisonChoices.LevelToleranceFor(settings, null));
    }

    /// <summary>
    /// A label from a newer server than this build knows comes back null rather than matching the
    /// nearest rung — a wrong save is worse than none, and the row is simply left alone.
    /// </summary>
    [Fact]
    public void AnUnrecognisedLabel_IsNotAChoice()
    {
        Assert.Null(JournalComparisonChoices.ToleranceFor(Settings(), "37 minutes-ish"));
    }

    /// <summary>
    /// Every published rung is inside the bounds the same response publishes, so a picker built
    /// from one can never offer a value validation would refuse.
    /// </summary>
    [Fact]
    public void EveryPublishedRung_IsInsideThePublishedBounds()
    {
        Assert.All(
            JournalComparison.SelectableToleranceMinutes,
            m => Assert.True(JournalComparison.IsSelectableTolerance(m)));

        Assert.All(
            JournalComparison.SelectableDirectionBoundMinutes,
            m => Assert.True(JournalComparison.IsSelectableDirectionBound(m)));

        Assert.All(
            JournalComparison.SelectableLevelTolerancePercents,
            p => Assert.True(JournalComparison.IsSelectableLevelTolerance(p)));
    }

    /// <summary>
    /// And every default is on its own ladder, so a member nobody has tuned opens a picker with
    /// their current value already on it rather than one that cannot show what is set.
    /// </summary>
    [Fact]
    public void EveryDefault_SitsOnItsOwnLadder()
    {
        Assert.Contains(
            JournalComparison.DefaultBedtimeToleranceMinutes,
            JournalComparison.SelectableToleranceMinutes);
        Assert.Contains(
            JournalComparison.DefaultWakeToleranceMinutes,
            JournalComparison.SelectableToleranceMinutes);
        Assert.Contains(
            JournalComparison.DefaultDirectionBoundMinutes,
            JournalComparison.SelectableDirectionBoundMinutes);
        Assert.Contains(
            JournalComparison.DefaultLevelTolerancePercent,
            JournalComparison.SelectableLevelTolerancePercents);
    }
}
