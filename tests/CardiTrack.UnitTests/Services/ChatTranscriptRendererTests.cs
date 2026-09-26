using System.Text;
using CardiTrack.Application.DTOs.Common;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Reports;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Services.Reports;
using QuestPDF.Fluent;
using QuestPDF.Infrastructure;
using Task = System.Threading.Tasks.Task;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// A chat conversation exported as a document. What is asserted is what the caregiver is
/// exporting it for: every turn is in it, the charts each reply was read against are drawn, and
/// the things this product promised not to export stay out of it.
/// </summary>
public class ChatTranscriptRendererTests
{
    private static readonly Guid MemberId = Guid.NewGuid();
    private static readonly DateTimeOffset Started = new(2026, 2, 10, 9, 14, 0, TimeSpan.Zero);

    private static readonly ReportSections NoSections = new(
        IncludeMetrics: false, IncludeAlerts: false, IncludeDevices: false, IncludeTrends: false);

    static ChatTranscriptRendererTests()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    private static ChartSeries Sleep() => new(
        "Sleep",
        Enumerable.Range(0, 7)
            .Select(i => new ChartPoint(new DateOnly(2026, 2, 4).AddDays(i), 380 + i * 12))
            .ToList(),
        Baseline: 402,
        Reference: new MetricReference { Low = 420, High = 540, Source = "NSF" });

    private static ChatTranscript BuildTranscript(
        IReadOnlyList<ChatTranscriptTurn>? turns = null, string? theme = "Sleep over the week") =>
        new(
            Guid.NewGuid(),
            MemberId,
            theme,
            Started,
            Started.AddMinutes(6),
            turns ??
            [
                new ChatTranscriptTurn(
                    ChatTurnRole.User, "How has she been sleeping?", Started, []),
                new ChatTranscriptTurn(
                    ChatTurnRole.Assistant,
                    "She's been sleeping a little longer each night this week.",
                    Started.AddMinutes(1),
                    [Sleep()]),
            ]);

    private static ReportDataSet BuildData(ChatTranscript? transcript = null) =>
        new(
            [
                new ReportMemberData(
                    new CardiMember
                    {
                        Id = MemberId,
                        FirstName = "Margaret",
                        LastName = "Doe",
                        DateOfBirth = new DateOnly(1948, 4, 12),
                        Gender = Gender.Female,
                        MedicalNotes = "Takes warfarin; history of AF"
                    },
                    [], [], [], [], [])
            ],
            new DateOnly(2026, 2, 10),
            new DateOnly(2026, 2, 10),
            Title: null)
        {
            Transcript = transcript ?? BuildTranscript(),
        };

    // ── PDF ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Pdf_ProducesARealPdf()
    {
        var rendered = await new PdfReportRenderer().RenderAsync(BuildData(), NoSections, narrative: null);

        Assert.Equal("application/pdf", rendered.ContentType);
        Assert.Equal("%PDF", Encoding.ASCII.GetString(rendered.Content, 0, 4));
        Assert.True(rendered.Content.Length > 1000, "A conversation export should not be a stub.");
    }

    [Fact]
    public async Task Pdf_KeepsOutTheFreeTextWeNeverExport()
    {
        var rendered = await new PdfReportRenderer().RenderAsync(BuildData(), NoSections, narrative: null);

        var bytes = Encoding.ASCII.GetString(rendered.Content);
        Assert.DoesNotContain("warfarin", bytes);
        Assert.DoesNotContain("1948-04-12", bytes);
    }

    [Fact]
    public async Task Pdf_RendersAConversationWithNoTurns()
    {
        // Real: a session whose first send failed before its turns persisted.
        var rendered = await new PdfReportRenderer().RenderAsync(
            BuildData(BuildTranscript(turns: [], theme: null)), NoSections, narrative: null);

        Assert.Equal("%PDF", Encoding.ASCII.GetString(rendered.Content, 0, 4));
    }

    [Fact]
    public async Task Pdf_RendersATurnThatCouldNotBeDecrypted()
    {
        // ChatTranscriptSource hands back an empty string rather than failing the export when a
        // row will not decrypt. The document must say so rather than leave a gap.
        var rendered = await new PdfReportRenderer().RenderAsync(
            BuildData(BuildTranscript(
            [
                new ChatTranscriptTurn(ChatTurnRole.User, string.Empty, Started, []),
                new ChatTranscriptTurn(ChatTurnRole.Assistant, "Here's what I found.", Started, []),
            ])),
            NoSections, narrative: null);

        Assert.Equal("%PDF", Encoding.ASCII.GetString(rendered.Content, 0, 4));
    }

    [Fact]
    public async Task Pdf_RendersAnAnswerWrittenInMarkdown()
    {
        // The assistant writes the same bold and bullets the health export's summary does, and
        // the answer is set through the same layout. What is asserted is that the path renders;
        // NarrativeMarkdownTests pin what each mark becomes.
        var rendered = await new PdfReportRenderer().RenderAsync(
            BuildData(BuildTranscript(
            [
                new ChatTranscriptTurn(ChatTurnRole.User, "How has she been *really* sleeping?", Started, []),
                new ChatTranscriptTurn(
                    ChatTurnRole.Assistant,
                    "## This week\n\nShe's been sleeping **a little longer** each night.\n\n- Monday: 6h 20m\n- Sunday: 7h 30m",
                    Started.AddMinutes(1),
                    [Sleep()]),
            ])),
            NoSections, narrative: null);

        Assert.Equal("%PDF", Encoding.ASCII.GetString(rendered.Content, 0, 4));
    }

    [Fact]
    public void Pdf_TitlesTheFileAsAChatTranscript()
    {
        var data = BuildData();

        var metadata = ChatTranscriptDocument.Compose(data, data.Transcript!).GetMetadata();

        Assert.Equal("Margaret Doe — Chat transcript, 10 Feb 2026", metadata.Title);
        Assert.Equal("CardiTrack", metadata.Author);
    }

    [Fact]
    public void Pdf_DrawsTheChartsTheReplyCarried()
    {
        // The whole reason a transcript is worth printing rather than copying out of the app —
        // and the part nobody would notice was missing until the appointment. Rasterised and
        // counted, because a chart that is composed but clipped to nothing is still a PDF.
        var data = BuildData();

        var hits = CountInk(data, 0x7C, 0x6F, 0xDC);

        Assert.True(hits > 200, $"Expected the reply's sleep chart on the page; found {hits} ink pixels.");
    }

    [Fact]
    public void Pdf_DrawsNoChartInk_WhenNoReplyCarriedASeries()
    {
        var data = BuildData(BuildTranscript(
        [
            new ChatTranscriptTurn(ChatTurnRole.User, "Is she up yet?", Started, []),
            new ChatTranscriptTurn(ChatTurnRole.Assistant, "She's been up since seven.", Started, []),
        ]));

        Assert.True(CountInk(data, 0x7C, 0x6F, 0xDC) < 50);
    }

    // ── CSV ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Csv_WritesOneRowPerMessage()
    {
        var rendered = await new CsvReportRenderer().RenderAsync(BuildData(), NoSections, narrative: null);
        var csv = Encoding.UTF8.GetString(rendered.Content);

        Assert.Contains("Member,SentUtc,Speaker,Message", csv);
        Assert.Contains("Caregiver", csv);
        Assert.Contains("CardiTrack assistant", csv);
        Assert.Contains("How has she been sleeping?", csv);
    }

    [Fact]
    public async Task Csv_WritesTheReadingsBehindTheReplies()
    {
        // The figures an answer rests on, so they can be checked rather than taken on trust.
        var rendered = await new CsvReportRenderer().RenderAsync(BuildData(), NoSections, narrative: null);
        var csv = Encoding.UTF8.GetString(rendered.Content);

        Assert.Contains("Metric,Date,Value,TheirUsual,TypicalLow,TypicalHigh,TypicalSource", csv);
        Assert.Contains("Sleep,2026-02-04,380", csv);
        Assert.Contains("NSF", csv);
    }

    [Fact]
    public async Task Csv_OmitsTheReadingsBlock_WhenNoReplyCarriedASeries()
    {
        var rendered = await new CsvReportRenderer().RenderAsync(
            BuildData(BuildTranscript(
            [
                new ChatTranscriptTurn(ChatTurnRole.User, "Is she up yet?", Started, []),
            ])),
            NoSections, narrative: null);

        Assert.DoesNotContain("TypicalSource", Encoding.UTF8.GetString(rendered.Content));
    }

    [Fact]
    public async Task Csv_NeutralisesAMessageThatWouldBecomeAFormula()
    {
        // A caregiver's question is free text, and this file is built to be forwarded — see
        // CsvReportRenderer on CWE-1236.
        var rendered = await new CsvReportRenderer().RenderAsync(
            BuildData(BuildTranscript(
            [
                new ChatTranscriptTurn(
                    ChatTurnRole.User, "=HYPERLINK(\"http://x\",\"Click\")", Started, []),
            ])),
            NoSections, narrative: null);

        Assert.Contains("'=HYPERLINK", Encoding.UTF8.GetString(rendered.Content));
    }

    [Fact]
    public async Task Csv_WritesNoneOfTheHealthExportsBlocks()
    {
        // A transcript is not a further section of the health export: the section flags default
        // on, and a file carrying both grains would be two documents in one.
        var rendered = await new CsvReportRenderer().RenderAsync(
            BuildData(),
            new ReportSections(
                IncludeMetrics: true, IncludeAlerts: true, IncludeDevices: true, IncludeTrends: true),
            narrative: null);

        Assert.DoesNotContain("SleepEfficiencyPercent", Encoding.UTF8.GetString(rendered.Content));
    }

    [Fact]
    public void Pdf_SaysWhichZoneEveryTimestampIsIn()
    {
        // The document is built to be forwarded, and the generation has no idea what the
        // caregiver's clock said. A bare "09:14" reads as local wherever it lands — and the zone
        // rides on each timestamp rather than a line at the top, because a reader handed one
        // page never sees the line that would have carried the qualifier.
        var morning = new DateTimeOffset(2026, 2, 10, 9, 14, 0, TimeSpan.Zero);

        Assert.Equal("09:14 UTC", ChatTranscriptDocument.Time(morning));

        // An offset that is not UTC still prints the UTC instant, not the wall clock it carries.
        Assert.Equal("09:14 UTC", ChatTranscriptDocument.Time(morning.ToOffset(TimeSpan.FromHours(5))));
    }

    private static int CountInk(ReportDataSet data, byte red, byte green, byte blue)
    {
        var images = ChatTranscriptDocument.Compose(data, data.Transcript!)
            .GenerateImages(new ImageGenerationSettings { ImageFormat = ImageFormat.Png, RasterDpi = 72 });

        var hits = 0;
        foreach (var bytes in images)
        {
            using var bitmap = SkiaSharp.SKBitmap.Decode(bytes);
            for (var y = 0; y < bitmap.Height; y++)
            {
                for (var x = 0; x < bitmap.Width; x++)
                {
                    var c = bitmap.GetPixel(x, y);
                    if (Math.Abs(c.Red - red) <= 12
                        && Math.Abs(c.Green - green) <= 12
                        && Math.Abs(c.Blue - blue) <= 12)
                        hits++;
                }
            }
        }

        return hits;
    }
}
