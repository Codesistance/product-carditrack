using CardiTrack.Application.Services;
using CardiTrack.Domain.Enums;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// Pins the severity contract between the model and the alert router: only an explicit
/// <c>Severity:</c> line routes, the conclusion beats the deliberation, and anything the
/// parser does not recognise yields no severity — never a guessed one.
/// </summary>
public class AssessmentSeverityParserTests
{
    [Theory]
    [InlineData("critical", AlertSeverity.Red)]
    [InlineData("high", AlertSeverity.Orange)]
    [InlineData("medium", AlertSeverity.Yellow)]
    [InlineData("low", AlertSeverity.Green)]
    public void EachSeverityWord_MapsOntoTheProductTaxonomy(string word, AlertSeverity expected)
    {
        var (raw, severity) = AssessmentSeverityParser.Parse(
            $"Heart rate looks steady.\nSeverity: {word}");

        Assert.Equal(word, raw);
        Assert.Equal(expected, severity);
    }

    [Theory]
    [InlineData("Severity: HIGH")]
    [InlineData("severity: high")]
    [InlineData("**Severity:** high")]
    [InlineData("**Severity: high**")]
    [InlineData("  Severity:   high")]
    public void TheLabel_SurvivesCase_Markdown_AndWhitespace(string line)
    {
        var (_, severity) = AssessmentSeverityParser.Parse($"All fine otherwise.\n{line}");

        Assert.Equal(AlertSeverity.Orange, severity);
    }

    // The prompt asks for the verdict as the closing line; a model that first *discusses* the
    // scale and then concludes must be read by its conclusion.
    [Fact]
    public void TheLastSeverityLine_Wins()
    {
        var (raw, severity) = AssessmentSeverityParser.Parse("""
            Severity: critical would mean seek help now, but this is not that.
            The rate settled within minutes.
            Severity: low
            """);

        Assert.Equal("low", raw);
        Assert.Equal(AlertSeverity.Green, severity);
    }

    // A severity word loose in prose is commentary, not a verdict — only the labelled line counts.
    [Fact]
    public void ASeverityWord_LooseInProse_DoesNotRoute()
    {
        var (raw, severity) = AssessmentSeverityParser.Parse(
            "The situation is not critical and the trend is low and steady.");

        Assert.Null(raw);
        Assert.Null(severity);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("No verdict here at all.")]
    [InlineData("Severity: banana")]
    [InlineData("Severity:")]
    public void AnythingUnrecognisable_YieldsNoSeverity(string output)
    {
        var (raw, severity) = AssessmentSeverityParser.Parse(output);

        Assert.Null(raw);
        Assert.Null(severity);
    }

    // "lowest", "highly" — a recognised word must end at a word boundary to count.
    [Fact]
    public void APrefixOfASeverityWord_DoesNotCount()
    {
        var (_, severity) = AssessmentSeverityParser.Parse("Severity: lowest");

        Assert.Null(severity);
    }
}
