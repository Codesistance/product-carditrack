using CardiTrack.Infrastructure.Services.Reports;
using static CardiTrack.Infrastructure.Services.Reports.NarrativeMarkdown;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// The narrative a model returns is Markdown whether or not it was asked to be. What the PDF
/// promises is that none of it reaches the page as punctuation: a heading is set as a heading,
/// bold as bold, a bullet as a bullet — and a mark the model used literally stays as text.
/// </summary>
public class NarrativeMarkdownTests
{
    private static string Text(IReadOnlyList<Run> runs) => string.Concat(runs.Select(r => r.Text));

    [Fact]
    public void Parse_SplitsHeadingsParagraphsAndLists()
    {
        var blocks = Parse("""
            ## Overall

            A steady month.
            Sleep held.

            ### What changed
            - Steps fell
            - Heart rate rose

            1. Mention it
            2. Watch it
            """);

        Assert.Collection(blocks,
            b => Assert.Equal((2, "Overall"), (Assert.IsType<Heading>(b).Level, Text(((Heading)b).Runs))),
            b => Assert.Equal("A steady month. Sleep held.", Text(Assert.IsType<Paragraph>(b).Runs)),
            b => Assert.Equal((3, "What changed"), (Assert.IsType<Heading>(b).Level, Text(((Heading)b).Runs))),
            b =>
            {
                var list = Assert.IsType<ListBlock>(b);
                Assert.False(list.Ordered);
                Assert.Equal(["Steps fell", "Heart rate rose"], list.Items.Select(Text));
            },
            b =>
            {
                var list = Assert.IsType<ListBlock>(b);
                Assert.True(list.Ordered);
                Assert.Equal(["Mention it", "Watch it"], list.Items.Select(Text));
            });
    }

    [Fact]
    public void Parse_RendersBoldAndItalic_AsStyleNotMarks()
    {
        var runs = ParseInlines("Her sleep was **much shorter** and *a little* broken.");

        Assert.Equal("Her sleep was much shorter and a little broken.", Text(runs));
        Assert.Contains(runs, r => r.Text == "much shorter" && r.Bold && !r.Italic);
        Assert.Contains(runs, r => r.Text == "a little" && r.Italic && !r.Bold);
        Assert.DoesNotContain(runs, r => r.Text.Contains('*'));
    }

    [Fact]
    public void Parse_AcceptsTheUnderscoreSpellings()
    {
        var runs = ParseInlines("__bold__ and _italic_");

        Assert.Contains(runs, r => r.Text == "bold" && r.Bold);
        Assert.Contains(runs, r => r.Text == "italic" && r.Italic);
    }

    [Fact]
    public void Parse_LeavesAnUnclosedMarkAsText()
    {
        // A literal asterisk — "5,000* steps" with a footnote — must not swallow the sentence.
        var runs = ParseInlines("About 5,000* steps on a good day.");

        Assert.Equal("About 5,000* steps on a good day.", Text(runs));
        Assert.All(runs, r => Assert.False(r.Bold || r.Italic));
    }

    [Fact]
    public void Parse_LeavesAnUnderscoreInsideAWordAlone()
    {
        var runs = ParseInlines("the DEVICE_STALE_LONG notice");

        Assert.Equal("the DEVICE_STALE_LONG notice", Text(runs));
    }

    [Fact]
    public void Parse_DropsRulesAndBackticks()
    {
        var blocks = Parse("Before\n\n---\n\nAfter `code`");

        Assert.Equal(2, blocks.Count);
        Assert.Equal("After code", Text(Assert.IsType<Paragraph>(blocks[1]).Runs));
    }

    [Fact]
    public void Parse_TreatsAWrappedListLineAsPartOfTheItem()
    {
        var blocks = Parse("- Her steps fell by half\n  over the same three days\n- Sleep held");

        var list = Assert.IsType<ListBlock>(Assert.Single(blocks));
        Assert.Equal(["Her steps fell by half over the same three days", "Sleep held"], list.Items.Select(Text));
    }

    [Fact]
    public void Parse_TreatsPlainProseAsOneParagraph()
    {
        // A model that ignored the structure and wrote prose still renders — as prose.
        var blocks = Parse("Sleep held near their usual.");

        Assert.Equal("Sleep held near their usual.", Text(Assert.IsType<Paragraph>(Assert.Single(blocks)).Runs));
    }

    [Fact]
    public void ToPlainText_StripsEveryMark()
    {
        var plain = ToPlainText("## Overall\n\nA **steady** month.\n\n- one\n- two");

        Assert.Equal("Overall\n\nA steady month.\n\none\ntwo", plain);
    }
}
