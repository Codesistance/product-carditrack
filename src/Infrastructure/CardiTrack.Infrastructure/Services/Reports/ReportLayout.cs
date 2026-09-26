using System.Globalization;
using System.Reflection;
using CardiTrack.Application.Reports;
using QuestPDF.Fluent;
using QuestPDF.Infrastructure;

namespace CardiTrack.Infrastructure.Services.Reports;

/// <summary>
/// The frame both PDF documents are drawn in — the running header and footer, the title block's
/// overline, the file's metadata — and the one Markdown layout both of them set generated text
/// with.
/// </summary>
/// <remarks>
/// <para>
/// Held in one place for the reason <see cref="ReportPalette"/> is: a caregiver may print the
/// health export, a journal export and a chat transcript and file them together, and three
/// documents from one product whose headers, title sizes and footers disagree read as three
/// products. Each document still lays out its own content; only what every page of every
/// document shares lives here.
/// </para>
/// <para>
/// The header names the person and the kind of document on every page, not the date range: the
/// range is in the title block, and a page separated from its first page is identified by who it
/// is about and what it is, which a range alone does not say.
/// </para>
/// </remarks>
internal static class ReportLayout
{
    /// <summary>The metrics export — readings, trends, alerts, notices.</summary>
    internal const string HealthExport = "Health export";

    /// <summary>The CardiJournal export — journal entries, with the trends they were read against.</summary>
    internal const string JournalExport = "CardiJournal export";

    /// <summary>A copy of one member-chat conversation — see <see cref="ChatTranscriptDocument"/>.</summary>
    internal const string ChatTranscript = "Chat transcript";

    /// <summary>
    /// One title size for every document. The health export's, which the transcript's longer
    /// titles take on two lines rather than overflowing — one size across a filed set matters more
    /// than one line per title.
    /// </summary>
    internal const float TitleSize = 22;

    /// <summary>One chart height for every document, so a trend and a chat reply's chart of the same
    /// metric are the same picture at the same scale.</summary>
    internal const float ChartHeight = 130;

    private const float LogoHeight = 14;

    /// <summary>
    /// The app icon's heart, cut down to a size a 14pt mark can use (112×96, about 440 dpi at that
    /// height) and embedded rather than read from disk: unlike the fonts in
    /// <see cref="ReportFonts"/> it is one small file every environment needs, and the runtime
    /// image has no other reason to carry it. Decoded once and shared, which is how QuestPDF
    /// writes one image object however many pages draw it.
    /// </summary>
    private static readonly Image LogoMark = LoadLogoMark();

    // ── Chrome ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Who the document is about, as the header, footer and file title name them: the member's
    /// full name, or a count where one export covers several people — a running header is not the
    /// place to list a family.
    /// </summary>
    internal static string Subject(ReportDataSet data) => data.Members.Count switch
    {
        0 => string.Empty,
        1 => data.Members[0].Member.FullName,
        var n => $"{n} people"
    };

    internal static void Header(IContainer container, string subject, string documentType) =>
        container.PaddingBottom(14).Column(column =>
        {
            column.Item().BorderBottom(1.5f).BorderColor(ReportPalette.Brand).PaddingBottom(6).Row(row =>
            {
                row.AutoItem().AlignMiddle().Height(LogoHeight)
                    .Image(LogoMark).FitHeight();
                row.AutoItem().PaddingLeft(5).AlignMiddle().Text("CardiTrack")
                    .FontSize(12).Bold().FontColor(ReportPalette.BrandDark);

                row.RelativeItem().AlignRight().AlignMiddle()
                    .Text(Joined(subject, documentType))
                    .FontSize(9).FontColor(ReportPalette.Secondary);
            });
        });

    /// <summary>
    /// The notice and the page count, with the person's name beside the count: printed pages get
    /// separated from each other, so the warning and the subject belong on each one rather than
    /// on a cover sheet.
    /// </summary>
    internal static void Footer(IContainer container, string notice, string subject) =>
        container.PaddingTop(8).BorderTop(1).BorderColor(ReportPalette.Divider).PaddingTop(5).Row(row =>
        {
            row.RelativeItem().Text(notice).FontSize(7.5f).FontColor(ReportPalette.Caption);

            row.AutoItem().PaddingLeft(12).AlignRight().Text(text =>
            {
                text.DefaultTextStyle(t => t.FontSize(7.5f).FontColor(ReportPalette.Caption));
                if (subject.Length > 0)
                    text.Span(subject + " · ");
                text.CurrentPageNumber();
                text.Span(" / ");
                text.TotalPages();
            });
        });

    /// <summary>The kind of document, set small over its title — the first words a reader meets.</summary>
    internal static void Overline(IContainer container, string documentType) =>
        container.Text(documentType.ToUpperInvariant())
            .FontSize(6.75f).Bold().LetterSpacing(0.08f).FontColor(ReportPalette.BrandDark);

    /// <summary>
    /// What a file manager, a mail client's preview and a screen reader say the file is before
    /// anyone opens it — otherwise QuestPDF's defaults, which name no one and nothing.
    /// </summary>
    internal static DocumentMetadata Metadata(
        string subject, string documentType, DateOnly from, DateOnly to) => new()
    {
        Title = subject.Length > 0
            ? $"{subject} — {documentType}, {Range(from, to)}"
            : $"{documentType}, {Range(from, to)}",
        Author = "CardiTrack",
        Subject = $"{documentType}. Confidential health information. Not a clinical assessment.",
        Creator = "CardiTrack",
        CreationDate = DateTimeOffset.UtcNow,
        ModifiedDate = DateTimeOffset.UtcNow,
    };

    /// <summary>
    /// A range in the short form the header used to carry — one date where the range is one day,
    /// since "20 Feb 2026 – 20 Feb 2026" says the same thing twice.
    /// </summary>
    internal static string Range(DateOnly from, DateOnly to) =>
        from == to
            ? from.ToString("d MMM yyyy", CultureInfo.InvariantCulture)
            : $"{from.ToString("d MMM yyyy", CultureInfo.InvariantCulture)} – {to.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}";

    private static string Joined(string subject, string documentType) =>
        subject.Length > 0 ? $"{subject} · {documentType}" : documentType;

    private static Image LoadLogoMark()
    {
        const string name = "CardiTrack.Infrastructure.Reports.carditrack-mark.png";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(
                $"The report logo resource '{name}' is missing from the assembly.");
        return Image.FromStream(stream);
    }

    // ── Generated text ──────────────────────────────────────────────────────────

    /// <summary>
    /// Generated Markdown laid out as formatting rather than printed as marks — the health
    /// export's summary and journal entries, and the transcript's answers. One layout, so the same
    /// model output reads the same in every document it lands in.
    /// </summary>
    internal static void Markdown(IContainer container, string markdown) =>
        container.Column(column =>
        {
            column.Spacing(6);
            foreach (var block in NarrativeMarkdown.Parse(markdown))
            {
                switch (block)
                {
                    case NarrativeMarkdown.Heading heading:
                        column.Item().PaddingTop(4).Text(text =>
                        {
                            text.DefaultTextStyle(t => t.FontSize(heading.Level <= 2 ? 11.5f : 10.5f).SemiBold().FontColor(ReportPalette.Ink));
                            Runs(text, heading.Runs);
                        });
                        break;

                    case NarrativeMarkdown.Paragraph paragraph:
                        column.Item().Text(text =>
                        {
                            text.DefaultTextStyle(t => t.LineHeight(1.45f));
                            Runs(text, paragraph.Runs);
                        });
                        break;

                    case NarrativeMarkdown.ListBlock list:
                        column.Item().Column(items =>
                        {
                            items.Spacing(3);
                            var n = 0;
                            foreach (var item in list.Items)
                            {
                                n++;
                                var marker = list.Ordered ? $"{n}." : "•";
                                items.Item().Row(row =>
                                {
                                    row.ConstantItem(16).AlignRight().PaddingRight(6)
                                        .Text(marker).FontColor(ReportPalette.Secondary);
                                    row.RelativeItem().Text(text =>
                                    {
                                        text.DefaultTextStyle(t => t.LineHeight(1.4f));
                                        Runs(text, item);
                                    });
                                });
                            }
                        });
                        break;
                }
            }
        });

    private static void Runs(TextDescriptor text, IReadOnlyList<NarrativeMarkdown.Run> runs)
    {
        foreach (var run in runs)
        {
            var span = text.Span(run.Text);
            if (run.Bold)
                span.SemiBold();
            if (run.Italic)
                span.Italic();
        }
    }
}
