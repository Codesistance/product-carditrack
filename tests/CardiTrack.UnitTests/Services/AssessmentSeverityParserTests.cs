using CardiTrack.Application.Services;
using CardiTrack.Domain.Enums;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// Pins the severity contract between the model's structured <c>severity</c> field and the alert
/// router: the four schema-suggested words map onto the product taxonomy, and anything else the
/// model returns yields no routable severity — never a guessed one.
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
        var (raw, severity) = AssessmentSeverityParser.Map(word);

        Assert.Equal(word, raw);
        Assert.Equal(expected, severity);
    }

    [Theory]
    [InlineData("HIGH")]
    [InlineData("High")]
    [InlineData("  high  ")]
    public void TheWord_SurvivesCaseAndWhitespace(string word)
    {
        var (_, severity) = AssessmentSeverityParser.Map(word);

        Assert.Equal(AlertSeverity.Orange, severity);
    }

    // The field is schema-constrained, not free text — the model has nowhere to put a severity
    // word except this field, so there is no "loose in prose" case left to guard against. What
    // remains is the model returning a word outside the four the schema asked for.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("banana")]
    [InlineData("lowest")]
    public void AnUnrecognisedWord_YieldsNoSeverity(string? word)
    {
        var (_, severity) = AssessmentSeverityParser.Map(word);

        Assert.Null(severity);
    }

    // Still recorded verbatim for audit even when it does not map — the model deviating from the
    // schema's suggested vocabulary is itself worth seeing, not worth erasing.
    [Fact]
    public void AnUnrecognisedWord_IsStillReturnedAsRaw_ForAudit()
    {
        var (raw, _) = AssessmentSeverityParser.Map("banana");

        Assert.Equal("banana", raw);
    }
}
