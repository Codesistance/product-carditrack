using System.Globalization;
using System.Text;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Reports;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Domain.Extensions;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace CardiTrack.Infrastructure.Services.Reports;

/// <summary>
/// The human-readable export: the document a caregiver prints and takes to an appointment.
/// </summary>
/// <remarks>
/// <para>
/// Structure is chosen for the reading it will actually get — a clinician glancing at it in a
/// consultation, and a family member filing it. So: the period's headline figures first, because
/// they answer "how is she" in one glance; then the AI narrative, because it says what happened in
/// plain language; then the trend charts; then the daily table, because that is what gets
/// questioned; then alerts. Every page says who it is about and what kind of document it is, is
/// numbered, and carries the confidentiality footer, since printed pages get separated.
/// </para>
/// <para>
/// This is the only format that carries the generated narrative, and it is labelled as generated.
/// A caregiver handing a document to a doctor must not have to guess which sentences a model
/// wrote — an unattributed AI summary in a medical setting is the failure mode worth designing
/// against.
/// </para>
/// </remarks>
public class PdfReportRenderer : IReportRenderer
{
    // The app's own palette, shared with the transcript document — see ReportPalette.
    private const string Ink = ReportPalette.Ink;
    private const string Body = ReportPalette.Body;
    private const string Secondary = ReportPalette.Secondary;
    private const string Caption = ReportPalette.Caption;
    private const string Divider = ReportPalette.Divider;
    private const string BrandDark = ReportPalette.BrandDark;
    private const string Tint = ReportPalette.Tint;
    private const string TableHead = ReportPalette.TableHead;
    private const string Zebra = ReportPalette.Zebra;
    private const string White = ReportPalette.White;

    private const float MarginHorizontalCm = 1.8f;
    private const float MarginVerticalCm = 1.4f;

    /// <summary>The content width, and so the chart width: the figure is drawn 1:1 in points.</summary>
    internal static readonly float ContentWidth =
        PageSizes.A4.Width - 2 * MarginHorizontalCm * 72 / 2.54f;

    private static readonly Metric[] Metrics =
    [
        new("Steps", "steps a day", "#1884DC", log => log.Steps, v => v.ToString("N0", CultureInfo.InvariantCulture),
            TickSteps: [1, 2, 5, 10, 25, 50, 100, 250, 500, 1000, 2000, 5000, 10000]),
        new("Resting heart rate", "bpm", "#E53E3E", log => log.RestingHeartRate, v => v.ToString("0", CultureInfo.InvariantCulture),
            TickSteps: [1, 2, 5, 10, 20, 25, 50]),
        new("Sleep", "a night", "#7C6FDC", log => log.SleepMinutes, v => SleepFigure((int)Math.Round(v)), TickSteps: [15, 30, 60, 120, 240]),
        new("Blood oxygen (SpO₂)", "%", "#1F8A72", log => (double?)log.SpO2Average, v => v.ToString("0.#", CultureInfo.InvariantCulture)),
    ];

    public ReportFormat Format => ReportFormat.Pdf;

    public Task<RenderedReport> RenderAsync(
        ReportDataSet data,
        ReportSections sections,
        string? narrative,
        CancellationToken ct = default) =>
        Task.FromResult(new RenderedReport(
            ChooseDocument(data, sections, narrative).GeneratePdf(), "application/pdf", "pdf"));

    /// <summary>
    /// A transcript export is a different document, not a further section of this one — see
    /// <see cref="ChatTranscriptDocument"/>. Branching here rather than registering a second
    /// renderer keeps one implementation per <see cref="ReportFormat"/>, which is the contract
    /// <c>ReportGenerationService</c> resolves renderers by.
    /// </summary>
    private static IDocument ChooseDocument(ReportDataSet data, ReportSections sections, string? narrative) =>
        data.Transcript is { } transcript
            ? ChatTranscriptDocument.Compose(data, transcript)
            : Compose(data, sections, narrative);

    /// <summary>The document before it is serialised — what a preview or a test renders pages from.</summary>
    internal static IDocument Compose(ReportDataSet data, ReportSections sections, string? narrative)
    {
        var subject = ReportLayout.Subject(data);
        var documentType = DocumentType(sections);

        return Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.MarginHorizontal(MarginHorizontalCm, Unit.Centimetre);
                    page.MarginVertical(MarginVerticalCm, Unit.Centimetre);
                    page.DefaultTextStyle(t => t
                        .FontFamily(ReportFonts.Families)
                        .FontSize(10).FontColor(Body).LineHeight(1.3f));

                    page.Header().Element(h => ReportLayout.Header(h, subject, documentType));
                    page.Content().Element(c => ComposeContent(c, data, sections, narrative, documentType));
                    // "Confidential health information" is the wording story 9.2 asks the footer
                    // for (docs/execution/ui/mobile/user_stories.md), so it is kept whole.
                    page.Footer().Element(f => ReportLayout.Footer(
                        f, "Confidential health information · Not a clinical assessment", subject));
                });
            })
            .WithMetadata(ReportLayout.Metadata(subject, documentType, data.From, data.To));
    }

    /// <summary>
    /// What kind of document this is, in the header, over the title and in the file's metadata.
    /// </summary>
    /// <remarks>
    /// Read off the sections rather than passed in, because the sections already are the request's
    /// shape: the journal screens send journals and nothing else (trends aside, which are the
    /// picture the entries were written against — see <c>JournalExportRequests</c>), and nothing
    /// else sends that. A health export whose caregiver ticked only journals is the same document,
    /// and calling it a journal export is the honest name for it too. Anything with a reading,
    /// alert, notice or device in it is a health export.
    /// </remarks>
    internal static string DocumentType(ReportSections sections) =>
        sections is
        {
            IncludeJournals: true,
            IncludeMetrics: false,
            IncludeAlerts: false,
            IncludeNotices: false,
            IncludeDevices: false
        }
            ? ReportLayout.JournalExport
            : ReportLayout.HealthExport;

    // ── Content ─────────────────────────────────────────────────────────────────

    private static void ComposeContent(
        IContainer container, ReportDataSet data, ReportSections sections, string? narrative,
        string documentType)
    {
        container.Column(column =>
        {
            column.Spacing(0);

            column.Item().Element(t => TitleBlock(t, data, documentType));

            foreach (var member in data.Members)
            {
                var period = member with { ActivityLogs = data.PeriodReadings(member) };

                // One member's facts sit straight under the title that already names them; a
                // family export names each member in front of their own.
                column.Item().PaddingTop(data.Members.Count > 1 ? 14 : 10).Element(m => MemberLine(
                    m, period, sections, PeriodDays(data), named: data.Members.Count > 1));

                if (sections.IncludeMetrics && period.ActivityLogs.Count > 0)
                    column.Item().PaddingTop(12).Element(k => KeyFigures(k, period));
            }

            if (!string.IsNullOrWhiteSpace(narrative))
                Section(column, "Summary", e => Summary(e, narrative));

            foreach (var member in data.Members)
            {
                var prefix = data.Members.Count > 1 ? $"{member.Member.FullName} · " : string.Empty;

                if (ShouldDrawTrends(sections, member, data.ChartFrom, data.ChartTo))
                    Section(column, prefix + "Trends", e => Charts(e, member, data.ChartFrom, data.ChartTo));

                if (sections.IncludeMetrics)
                {
                    var periodLogs = data.PeriodReadings(member);
                    if (periodLogs.Count > 0)
                        Section(column, prefix + "Daily readings",
                            e => DailyTable(e, member with { ActivityLogs = periodLogs }));
                    else
                        Section(column, prefix + "Daily readings", e => EmptyState(e, "No readings were recorded in this period."));
                }

                // Before the alerts, because it is the frame they should be read in: an alert
                // says one day was unusual, and this says what usual is for this person.
                var comparison = ReportComparison.For(
                    member with { ActivityLogs = data.PeriodReadings(member) },
                    member.Member.DateOfBirth.ToAgeInYears(data.To));
                if (sections.IncludeMetrics && comparison.Count > 0)
                    Section(column, prefix + "How this compares", e => ComparisonTable(e, comparison));

                if (sections.IncludeAlerts && member.Alerts.Count > 0)
                    Section(column, prefix + "Alerts", e => AlertsTable(e, member));

                if (sections.IncludeJournals && member.Journals.Count > 0)
                    Section(column, prefix + "Journals", e => Journals(e, member));

                if (sections.IncludeNotices && member.Notices.Count > 0)
                    Section(column, prefix + "Notices", e => NoticesTable(e, member));
            }
        });
    }

    /// <summary>
    /// A titled section. The title and the start of its body move to the next page together: a
    /// heading alone at the foot of a page, with its table overleaf, is the commonest way a
    /// generated document looks unfinished.
    /// </summary>
    private static void Section(ColumnDescriptor column, string title, Action<IContainer> body) =>
        column.Item().PaddingTop(22).EnsureSpace(120).Column(section =>
        {
            section.Item().Element(e => SectionTitle(e, title));
            section.Item().Element(body);
        });

    /// <summary>
    /// The kind of document, then who and when it is about, then the period's bare facts.
    /// </summary>
    /// <remarks>
    /// The heading is composed here rather than taken from the request's title. The request's
    /// title is the history list's label for the export, written by whichever screen queued it
    /// ("Margaret Okafor — health export", "Margaret Okafor — Daybooks"); printed as the heading it
    /// repeated the header and said nothing about the period. The heading names the member and
    /// the period instead, the same way whichever screen asked for it.
    /// </remarks>
    private static void TitleBlock(IContainer container, ReportDataSet data, string documentType) =>
        container.Column(column =>
        {
            column.Item().Element(e => ReportLayout.Overline(e, documentType));

            column.Item().PaddingTop(3).Text(Title(data))
                .FontSize(ReportLayout.TitleSize).Bold().FontColor(Ink).LineHeight(1.1f);

            column.Item().PaddingTop(4).Text(
                    $"{Date(data.From, "d MMMM yyyy")} to {Date(data.To, "d MMMM yyyy")}"
                    + $"  ·  {Days(PeriodDays(data))}  ·  Prepared {Date(DateTime.UtcNow, "d MMMM yyyy")}")
                .FontSize(10).FontColor(Secondary);
        });

    /// <summary>
    /// "Margaret's September 2026" where the range is exactly one calendar month — the shape every
    /// Monthbook export and most appointment printouts take — and "Margaret's 28 Aug 2026 – 26 Sep
    /// 2026" otherwise. A family export has no one first name to lead with, so its heading is the
    /// period alone; each member is named on their own line below it.
    /// </summary>
    internal static string Title(ReportDataSet data)
    {
        var period = IsCalendarMonth(data.From, data.To)
            ? Date(data.From, "MMMM yyyy")
            : ReportLayout.Range(data.From, data.To);

        if (data.Members.Count != 1)
            return period;

        var member = data.Members[0].Member;
        var name = string.IsNullOrWhiteSpace(member.FirstName) ? member.FullName.Trim() : member.FirstName.Trim();
        return name.Length > 0 ? $"{name}'s {period}" : period;
    }

    private static bool IsCalendarMonth(DateOnly from, DateOnly to) =>
        from.Day == 1
        && to.Year == from.Year && to.Month == from.Month
        && to.Day == DateTime.DaysInMonth(to.Year, to.Month);

    private static int PeriodDays(ReportDataSet data) => data.To.DayNumber - data.From.DayNumber + 1;

    private static string Days(int days) => days == 1 ? "1 day" : $"{days} days";

    /// <summary>
    /// The facts about a member: who the document is about, and where the numbers came from —
    /// device types only, never the caregiver's label for a device
    /// (docs/technical/data_protection_architecture.md §70).
    /// </summary>
    /// <remarks>
    /// Each fact is only stated where the export actually carries the data behind it. A caregiver
    /// who unticked metrics gets no readings gathered at all, and "0 of 30 days measured" printed
    /// over that absence would report a healthy member as an inactive one — the same mistake as
    /// printing a zero for a day the watch was not worn.
    /// </remarks>
    /// <param name="periodDays">The days the export covers, which the measured count is out of.</param>
    internal static IReadOnlyList<string> MemberFacts(
        ReportMemberData member, ReportSections sections, int periodDays)
    {
        var age = AgeAt(member.Member.DateOfBirth, DateOnly.FromDateTime(DateTime.UtcNow));
        var facts = new List<string> { age.ToString(CultureInfo.InvariantCulture), SexLabel(member.Member.Gender) };

        if (sections.IncludeDevices && member.Devices.Count > 0)
        {
            var types = member.Devices
                .Select(d => d.DeviceType.GetDisplayName())
                .Distinct()
                .OrderBy(t => t, StringComparer.Ordinal);
            facts.Add(string.Join(", ", types));
        }

        if (sections.IncludeMetrics)
        {
            var measured = member.ActivityLogs.Count(HasAnyReading);
            facts.Add($"{measured} of {Days(periodDays)} measured");
        }

        return facts;
    }

    /// <summary>
    /// The member's facts as one quiet line under the title. It was a tinted banner with the name
    /// set large inside it, which on a one-member export said the name a third time — after the
    /// header and the title — in the most prominent box on the page.
    /// </summary>
    private static void MemberLine(
        IContainer container, ReportMemberData member, ReportSections sections, int periodDays, bool named) =>
        container.Text(text =>
        {
            text.DefaultTextStyle(t => t.FontSize(9).FontColor(Caption));
            if (named)
                text.Span(member.Member.FullName + "   ").FontSize(11).SemiBold().FontColor(Ink);
            text.Span(string.Join("  ·  ", MemberFacts(member, sections, periodDays)));
        });

    /// <summary>
    /// The period in four numbers. An average answers the question a caregiver is asked first —
    /// "how has she been" — and the range under it says whether that average hides anything.
    /// </summary>
    private static void KeyFigures(IContainer container, ReportMemberData member) =>
        container.Row(row =>
        {
            row.Spacing(8);
            foreach (var metric in Metrics)
            {
                var values = member.ActivityLogs
                    .Select(metric.Read)
                    .Where(v => v is not null)
                    .Select(v => v!.Value)
                    .ToList();

                row.RelativeItem().Element(tile => StatTile(tile, metric, values));
            }
        });

    /// <summary>
    /// One key figure. The metric's colour is a rule across the tile's top edge — the same colour
    /// its trend is drawn in further down — rather than a dot beside the title, which at 8pt read
    /// as a status light.
    /// </summary>
    private static void StatTile(IContainer container, Metric metric, IReadOnlyList<double> values) =>
        container
            .Border(1).BorderColor(Divider).CornerRadius(6)
            .Column(tile =>
            {
                tile.Item().Height(2).Background(metric.Color);

                tile.Item().PaddingTop(6).PaddingBottom(8).PaddingHorizontal(10).Column(column =>
                {
                    column.Item().Text(metric.Title).FontSize(8.5f).FontColor(Secondary);

                    if (values.Count == 0)
                    {
                        column.Item().PaddingTop(3).Text("—").FontSize(17).SemiBold().FontColor(Caption);
                        column.Item().Text("not measured").FontSize(7.5f).FontColor(Caption);
                        return;
                    }

                    column.Item().PaddingTop(3).Text(text =>
                    {
                        text.Span(metric.Format(values.Average())).FontSize(17).SemiBold().FontColor(Ink);
                        text.Span("  " + metric.Unit).FontSize(8).FontColor(Secondary);
                    });

                    column.Item().Text(
                            $"{metric.Format(values.Min())} – {metric.Format(values.Max())}  ·  {values.Count} d")
                        .FontSize(7.5f).FontColor(Caption);
                });
            });

    private static void SectionTitle(IContainer container, string title) =>
        container
            .BorderBottom(1).BorderColor(Divider)
            .PaddingBottom(4)
            .Text(title).FontSize(13).SemiBold().FontColor(Ink);

    private static void EmptyState(IContainer container, string message) =>
        container.PaddingTop(8).Text(message).FontSize(9.5f).Italic().FontColor(Secondary);

    // ── Narrative ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The narrative under its own heading — laid out through <see cref="Section"/> like every
    /// other section, so the word "Summary" cannot strand itself at the foot of a page with the
    /// paragraph it introduces overleaf.
    /// </summary>
    private static void Summary(IContainer container, string narrative) =>
        container.PaddingTop(8).Column(column =>
        {
            column.Item().Element(e => ReportLayout.Markdown(e, narrative));

            // Attribution sits with the text it qualifies, not in a footnote a reader skips.
            column.Item().PaddingTop(10).Element(e => AiAttribution(e,
                "Written by CardiTrack's AI assistant from the readings in this document. "
                + "It is not a clinical assessment."));
        });

    /// <summary>The "AI" mark and a sentence saying what it wrote — set over or under the text it
    /// qualifies, never in a footnote a reader skips.</summary>
    private static void AiAttribution(IContainer container, string sentence) =>
        container
            .Background(Tint).CornerRadius(4)
            .PaddingVertical(5).PaddingHorizontal(8)
            .Row(row =>
            {
                row.AutoItem().AlignMiddle()
                    .Background(BrandDark).CornerRadius(3).PaddingVertical(1).PaddingHorizontal(4)
                    .Text("AI").FontSize(6.5f).Bold().FontColor(Colors.White);
                row.RelativeItem().PaddingLeft(7).AlignMiddle().Text(sentence)
                    .FontSize(8).Italic().FontColor(Secondary);
            });

    // ── Charts ──────────────────────────────────────────────────────────────────

    private static void Charts(IContainer container, ReportMemberData member, DateOnly from, DateOnly to) =>
        container.Column(column =>
        {
            column.Spacing(10);
            foreach (var metric in Metrics)
            {
                var chart = ReportChartRenderer.Line(
                    member.ActivityLogs, metric.Read, from, to, metric.Color, metric.Format,
                    ContentWidth, ReportLayout.ChartHeight, metric.TickSteps);
                if (chart is null)
                    continue;

                var values = member.ActivityLogs
                    .Where(l => l.Date >= from && l.Date <= to)
                    .Select(metric.Read)
                    .Where(v => v is not null)
                    .Select(v => v!.Value)
                    .ToList();

                // A chart split across a page break is two half-charts; keep each one whole.
                column.Item().ShowEntire().PaddingTop(8).Column(block =>
                {
                    block.Item().Row(row =>
                    {
                        row.ConstantItem(8).AlignMiddle().Height(8).Width(8)
                            .Background(metric.Color).CornerRadius(4);
                        row.RelativeItem().PaddingLeft(6).Text(text =>
                        {
                            text.Span(metric.Title).FontSize(10).SemiBold().FontColor(Ink);
                            text.Span("  " + metric.Unit).FontSize(8.5f).FontColor(Secondary);
                        });
                        row.RelativeItem().AlignRight().Text(
                                $"average {metric.Format(values.Average())}  ·  {values.Count} of {to.DayNumber - from.DayNumber + 1} days")
                            .FontSize(8.5f).FontColor(Caption);
                    });
                    block.Item().PaddingTop(4).Element(e => Figure(e, chart));
                });
            }
        });

    /// <summary>
    /// The marks as vector geometry with the labels set over them in the document's own face —
    /// see <see cref="ReportChartRenderer"/> for why the SVG carries no text of its own.
    /// </summary>
    internal static void Figure(IContainer container, ReportChartRenderer.Chart chart) =>
        container.Width(chart.Width).Height(chart.Height).Layers(layers =>
        {
            layers.PrimaryLayer().Svg(chart.Svg).FitArea();

            foreach (var label in chart.Labels)
            {
                const float lineHeight = 12;   // 8pt semibold at the default leading needs a little over 10
                var size = label.Emphasis ? 8f : 7.5f;

                // The text box stays inside the figure — a layer is clipped to it, and the
                // end-of-line value sits close enough to the right edge for that to matter.
                var box = label.Anchor switch
                {
                    ReportChartRenderer.Anchor.Start => chart.Width - label.X,
                    ReportChartRenderer.Anchor.Middle => Math.Min(64, 2 * Math.Min(label.X, chart.Width - label.X)),
                    _ => Math.Min(64, label.X)
                };
                var x = label.Anchor switch
                {
                    ReportChartRenderer.Anchor.Start => label.X,
                    ReportChartRenderer.Anchor.Middle => label.X - box / 2,
                    _ => label.X - box
                };

                var cell = layers.Layer().Unconstrained()
                    .OffsetX(x).OffsetY(label.Y - lineHeight / 2)
                    .Width(box).Height(lineHeight);
                cell = label.Anchor switch
                {
                    ReportChartRenderer.Anchor.Start => cell.AlignLeft(),
                    ReportChartRenderer.Anchor.Middle => cell.AlignCenter(),
                    _ => cell.AlignRight()
                };

                var text = cell.AlignMiddle().Text(label.Text).FontSize(size);
                if (label.Emphasis)
                    text.SemiBold().FontColor(Body);
                else
                    text.FontColor(Secondary);
            }
        });

    // ── Tables ──────────────────────────────────────────────────────────────────

    private static void DailyTable(IContainer container, ReportMemberData member) =>
        container.PaddingTop(6).Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.RelativeColumn(2.4f); // Date
                columns.RelativeColumn(1.4f); // Steps
                columns.RelativeColumn(1.4f); // Active
                columns.RelativeColumn(1.6f); // Resting HR
                columns.RelativeColumn(1.6f); // Sleep
                columns.RelativeColumn(1.2f); // SpO2
            });

            table.Header(header =>
            {
                HeaderCell(header.Cell(), "Date", right: false);
                HeaderCell(header.Cell(), "Steps");
                HeaderCell(header.Cell(), "Active");
                HeaderCell(header.Cell(), "Resting HR");
                HeaderCell(header.Cell(), "Sleep");
                HeaderCell(header.Cell(), "SpO₂");
            });

            var index = 0;
            foreach (var log in member.ActivityLogs.OrderBy(l => l.Date))
            {
                var shade = index++ % 2 == 1 ? Zebra : White;
                var quiet = !HasAnyReading(log);

                BodyCell(table.Cell(), log.Date.ToString("ddd d MMM", CultureInfo.InvariantCulture), shade, right: false, quiet);
                BodyCell(table.Cell(), Figure(log.Steps), shade, quiet: quiet);
                BodyCell(table.Cell(), Figure(log.ActiveMinutes, " min"), shade, quiet: quiet);
                BodyCell(table.Cell(), Figure(log.RestingHeartRate, " bpm"), shade, quiet: quiet);
                BodyCell(table.Cell(), Sleep(log.SleepMinutes), shade, quiet: quiet);
                BodyCell(table.Cell(), Figure(log.SpO2Average, "%"), shade, quiet: quiet);
            }
        });

    /// <summary>
    /// Each metric over the period against this member's own usual and the published band.
    /// </summary>
    /// <remarks>
    /// Drawn by the renderer from computed rows, not written by the model. The narrative above is
    /// given the same figures, but a model call can fail, time out or hedge, and a document whose
    /// only comparison was a sentence would then carry a column of numbers and nothing to read
    /// them against. The band names its publisher in the cell rather than in a footnote, because
    /// an unattributed range in a document a caregiver may hand to a clinician reads as ours.
    /// </remarks>
    private static void ComparisonTable(
        IContainer container, IReadOnlyList<ReportComparisonRow> rows) =>
        container.PaddingTop(6).Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.RelativeColumn(5);
                columns.RelativeColumn(3);
                columns.RelativeColumn(3);
                columns.RelativeColumn(3);
                columns.RelativeColumn(5);
            });

            table.Header(header =>
            {
                HeaderCell(header.Cell(), "Metric", right: false);
                HeaderCell(header.Cell(), "This period", right: true);
                HeaderCell(header.Cell(), "Their usual", right: true);
                HeaderCell(header.Cell(), "Change", right: true);
                HeaderCell(header.Cell(), "Published range", right: false);
            });

            var index = 0;
            foreach (var row in rows)
            {
                var shade = index++ % 2 == 1 ? Zebra : White;
                BodyCell(table.Cell(), $"{row.Metric} ({row.Unit})", shade, right: false);
                BodyCell(table.Cell(), Figure(row.PeriodAverage), shade, right: true);
                BodyCell(table.Cell(), row.Usual is { } usual ? Figure(usual) : "—", shade, right: true);
                BodyCell(table.Cell(), ChangeText(row.ChangePercent), shade, right: true);
                BodyCell(table.Cell(), BandText(row), shade, right: false);
            }
        });

    /// <summary>Trailing zeros dropped: 7.0 hours is 7, and 6.4 stays 6.4.</summary>
    private static string Figure(decimal value) =>
        value.ToString("0.#", CultureInfo.InvariantCulture);

    /// <summary>
    /// The change in words as well as sign. An arrow or a bare minus leaves the reader to decide
    /// whether down is good, and for sleep and steps it is not the same answer as for heart rate.
    /// </summary>
    private static string ChangeText(decimal? changePercent) => changePercent switch
    {
        null => "—",
        0 => "level",
        < 0 => $"{Math.Abs(changePercent.Value):0}% below",
        _ => $"{changePercent.Value:0}% above",
    };

    private static string BandText(ReportComparisonRow row) =>
        row is { BandLow: { } low, BandHigh: { } high, BandSource: { } source }
            ? $"{Figure(low)}-{Figure(high)} ({source})"
            : "no published range";

    private static void AlertsTable(IContainer container, ReportMemberData member) =>
        container.PaddingTop(6).Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.RelativeColumn(2);
                columns.RelativeColumn(2);
                columns.RelativeColumn(6);
            });

            table.Header(header =>
            {
                HeaderCell(header.Cell(), "Date", right: false);
                HeaderCell(header.Cell(), "Severity", right: false);
                HeaderCell(header.Cell(), "Alert", right: false);
            });

            var index = 0;
            foreach (var alert in member.Alerts.OrderByDescending(a => a.TriggeredDate))
            {
                var shade = index++ % 2 == 1 ? Zebra : White;
                BodyCell(table.Cell(), alert.TriggeredDate.ToString("d MMM yyyy", CultureInfo.InvariantCulture), shade, right: false);
                table.Cell().Background(shade).BorderBottom(1).BorderColor(Divider)
                    .PaddingVertical(4).PaddingHorizontal(6)
                    .Element(c => SeverityChip(c, alert.Severity));
                BodyCell(table.Cell(), alert.Title, shade, right: false);
            }
        });

    /// <summary>
    /// Severity as its word on a wash of its colour — never the colour alone, which a photocopier
    /// and a colour-blind reader both lose, and never the colour's name, which is how the table
    /// used to print it: "Orange" is how the rules grade an alert, not what it means to a reader.
    /// </summary>
    /// <remarks>
    /// The ink is the severity's own hue taken dark enough to clear 4.5:1 on its wash and on the
    /// zebra row behind it; the wash is the app's status colour at about 12%, a shade lighter than
    /// the app's 15% pills so that the dark ink still clears on it.
    /// </remarks>
    private static void SeverityChip(IContainer container, AlertSeverity severity)
    {
        var (wash, ink) = SeverityColors(severity);
        container.AlignLeft().AlignMiddle()
            .Background(wash).CornerRadius(3).PaddingVertical(1.5f).PaddingHorizontal(6)
            .Text(SeverityWord(severity)).FontSize(8).SemiBold().FontColor(ink);
    }

    /// <summary>
    /// The app's words for the four grades (AlertSeverityLook on mobile): yellow is Notice, not
    /// Info, because "something is different" and "nothing to report" are different news. An
    /// ungraded alert reads as Info, as it does in the app.
    /// </summary>
    internal static string SeverityWord(AlertSeverity severity) => severity switch
    {
        AlertSeverity.Red => "Critical",
        AlertSeverity.Orange => "Urgent",
        AlertSeverity.Yellow => "Notice",
        _ => "Info"
    };

    private static (string Wash, string Ink) SeverityColors(AlertSeverity severity) => severity switch
    {
        AlertSeverity.Red => ("#FCE8E8", "#B42828"),
        AlertSeverity.Orange => ("#FDEFE6", "#9A4A0E"),
        AlertSeverity.Yellow => ("#FDF5E6", "#7A5210"),
        AlertSeverity.Green => ("#E7F7F3", "#15644F"),
        _ => (TableHead, Caption)
    };

    /// <summary>
    /// The journal entries, each on its own card, under one statement of who wrote them.
    /// </summary>
    /// <remarks>
    /// The AI attribution is said once, at the head of the section, rather than repeated at the
    /// foot of every card: every entry has the same author, and a stack of identical italic lines
    /// reads as boilerplate and gets skipped — the opposite of what a disclosure is for. The one
    /// line sits where the reader starts the section, as the summary's does, which is what
    /// docs/compliance/ai_act_classification.md §7.1 asks for: say it is AI-written where it is
    /// shown.
    /// </remarks>
    private static void Journals(IContainer container, ReportMemberData member) =>
        container.PaddingTop(8).Column(column =>
        {
            column.Spacing(10);
            column.Item().Element(e => AiAttribution(e,
                "Every entry below was written by CardiTrack's AI assistant. Not a clinical assessment."));

            foreach (var entry in member.Journals.OrderBy(j => j.LocalDate))
            {
                // Kept whole where it fits — moved to the next page rather than split — and let
                // run on where it cannot fit on any page. ShowEntire, which this was, failed the
                // whole export on an entry longer than a page, and a Monthbook entry near its
                // 4,000-character ceiling in a card this narrow comes close to one.
                column.Item().PreventPageBreak()
                    .Border(1).BorderColor(Divider).CornerRadius(6)
                    .PaddingVertical(9).PaddingHorizontal(12)
                    .Column(card =>
                    {
                        card.Item().Row(row =>
                        {
                            row.AutoItem().AlignMiddle()
                                .Background(Tint).CornerRadius(3).PaddingVertical(1.5f).PaddingHorizontal(6)
                                .Text(BookName(entry.Audience)).FontSize(7.5f).SemiBold().FontColor(BrandDark);
                            row.AutoItem().PaddingLeft(8).AlignMiddle()
                                .Text(entry.LocalDate.ToString("d MMMM yyyy", CultureInfo.InvariantCulture))
                                .FontSize(8.5f).FontColor(Secondary);
                        });

                        if (!string.IsNullOrWhiteSpace(entry.Headline))
                            card.Item().PaddingTop(5).Text(entry.Headline).FontSize(11).SemiBold().FontColor(Ink);

                        card.Item().PaddingTop(4).Element(e => ReportLayout.Markdown(e, entry.Text));
                    });
            }
        });

    private static void NoticesTable(IContainer container, ReportMemberData member) =>
        container.PaddingTop(6).Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.RelativeColumn(2);
                columns.RelativeColumn(2);
                columns.RelativeColumn(4);
                columns.RelativeColumn(2);
            });

            table.Header(header =>
            {
                HeaderCell(header.Cell(), "Date", right: false);
                HeaderCell(header.Cell(), "Category", right: false);
                HeaderCell(header.Cell(), "Notice", right: false);
                HeaderCell(header.Cell(), "State", right: false);
            });

            var index = 0;
            foreach (var notice in member.Notices.OrderByDescending(n => n.FirstDetectedDate))
            {
                var shade = index++ % 2 == 1 ? Zebra : White;
                BodyCell(table.Cell(), notice.FirstDetectedDate.ToString("d MMM yyyy", CultureInfo.InvariantCulture), shade, right: false);
                BodyCell(table.Cell(), Humanise(notice.Category), shade, right: false);
                BodyCell(table.Cell(), NoticeLabel(notice.RuleCode), shade, right: false);
                BodyCell(table.Cell(), Humanise(notice.State), shade, right: false);
            }
        });

    private static void HeaderCell(IContainer cell, string text, bool right = true)
    {
        var box = cell.Background(TableHead).BorderBottom(1).BorderColor("#CBD5E1")
            .PaddingVertical(5).PaddingHorizontal(6);
        (right ? box.AlignRight() : box).Text(text).SemiBold().FontSize(8.5f).FontColor(Secondary);
    }

    private static void BodyCell(IContainer cell, string text, string shade, bool right = true, bool quiet = false)
    {
        var box = cell.Background(shade).BorderBottom(1).BorderColor(Divider)
            .PaddingVertical(4).PaddingHorizontal(6);
        (right ? box.AlignRight() : box).Text(text).FontSize(9).FontColor(quiet ? Caption : Body);
    }

    // ── Words and figures ───────────────────────────────────────────────────────

    private static string BookName(DigestAudience audience) => audience switch
    {
        DigestAudience.Weekbook => "Weekbook",
        DigestAudience.Monthbook => "Monthbook",
        _ => "Daybook"
    };

    /// <summary>
    /// An enum member as words in sentence case — <c>CheckIn</c> as "Check in" — so a table cell
    /// reads as English rather than as the identifier it came from: what <see cref="NoticeLabel"/>
    /// does for a rule code, done for PascalCase.
    /// </summary>
    internal static string Humanise(Enum value)
    {
        var name = value.ToString();
        var words = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            // A new word at a lower-to-upper step (CheckIn), and at the last capital of a run
            // that a lower-case letter follows (HRVDrop is "HRV drop").
            var boundary = i > 0 && char.IsUpper(c)
                && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1])
                    || (char.IsUpper(name[i - 1]) && i + 1 < name.Length && char.IsLower(name[i + 1])));
            if (boundary)
                words.Append(' ');
            words.Append(c);
        }

        // Sentence case, but an acronym stays an acronym.
        var parts = words.ToString().Split(' ');
        for (var i = 1; i < parts.Length; i++)
        {
            if (parts[i].Length == 1 || parts[i].Any(char.IsLower))
                parts[i] = parts[i].ToLowerInvariant();
        }

        return string.Join(' ', parts);
    }

    /// <summary>
    /// Rule codes are catalogue keys (<c>DEVICE_STALE_LONG</c>), not caregiver free text.
    /// Rendered as words so the table is readable without becoming a localization dump.
    /// </summary>
    private static string NoticeLabel(string ruleCode)
    {
        var words = ruleCode.Replace("_", " ", StringComparison.Ordinal).ToLowerInvariant();
        return words.Length == 0 ? words : char.ToUpperInvariant(words[0]) + words[1..];
    }

    /// <summary>
    /// Every date in the document, in one culture. The tables and the chart axes were already
    /// invariant and the header was not, so a service running under a non-English culture printed
    /// a localised header above English axis labels — two languages on one page of a document a
    /// clinician reads. Invariant rather than the caregiver's locale because no locale reaches
    /// this renderer; when one does, it belongs here, in one place.
    /// </summary>
    private static string Date(DateOnly date, string format) =>
        date.ToString(format, CultureInfo.InvariantCulture);

    private static string Date(DateTime date, string format) =>
        date.ToString(format, CultureInfo.InvariantCulture);

    private static string SexLabel(Gender gender) => gender switch
    {
        Gender.Male => "Male",
        Gender.Female => "Female",
        _ => "Sex not stated"
    };

    private static bool HasAnyReading(ActivityLog log) =>
        log.Steps is not null || log.ActiveMinutes is not null || log.RestingHeartRate is not null
        || log.SleepMinutes is not null || log.SpO2Average is not null;

    /// <summary>
    /// Graphs ticked and at least one of the four figures we actually plot
    /// inside the chart window. A day that only has active minutes would pass
    /// <see cref="HasAnyReading"/> and then produce a Trends heading with no
    /// marks. A reading outside <paramref name="from"/>–<paramref name="to"/>
    /// is the same: Charts would clip it and leave an empty heading.
    /// </summary>
    internal static bool ShouldDrawTrends(
        ReportSections sections, ReportMemberData member, DateOnly from, DateOnly to) =>
        sections.IncludeTrends && HasAChartedReading(member, from, to);

    /// <summary>
    /// At least one of the four figures we actually plot, on a day inside
    /// the window the figure will draw.
    /// </summary>
    internal static bool HasAChartedReading(
        ReportMemberData member, DateOnly from, DateOnly to) =>
        Metrics.Any(metric => member.ActivityLogs.Any(log =>
            log.Date >= from && log.Date <= to && metric.Read(log) is not null));

    /// <summary>
    /// A reading the device never reported prints as an em dash, not a blank and never a zero —
    /// "no steps recorded" and "did not move today" are different facts in a document a clinician
    /// may act on.
    /// </summary>
    private static string Figure(int? value, string suffix = "") =>
        value is { } v ? v.ToString("N0", CultureInfo.InvariantCulture) + suffix : "—";

    private static string Figure(decimal? value, string suffix = "") =>
        value is { } v ? v.ToString("0.#", CultureInfo.InvariantCulture) + suffix : "—";

    private static string Sleep(int? minutes) =>
        minutes is { } m ? SleepFigure(m) : "—";

    private static string SleepFigure(int minutes) => $"{minutes / 60}h {minutes % 60:00}m";

    /// <summary>
    /// Whole years at the given date. Rendered rather than the date of birth itself: age is what
    /// a reference range is read against, and it is one category less identifying than a birth
    /// date in a document that may be photocopied.
    /// </summary>
    private static int AgeAt(DateOnly dateOfBirth, DateOnly on)
    {
        var age = on.Year - dateOfBirth.Year;
        return on < dateOfBirth.AddYears(age) ? age - 1 : age;
    }

    /// <summary>One charted, tiled metric: where it comes from, its colour, and how a value prints.</summary>
    /// <param name="TickSteps">Axis steps in the metric's unit. Every metric counted in whole
    /// units names its own ladder, because the open 1–2–5 sequence can land on a fraction —
    /// a member flat at zero steps produced a 0.5 step, and four ticks printed through an integer
    /// format read "0, 0, 1, 2". Sleep names one for a second reason: it is stored in minutes and
    /// is read in half hours, not in fifties. Null leaves a metric on the open sequence, which is
    /// right where fractions are meaningful — SpO₂ prints a decimal.</param>
    private sealed record Metric(
        string Title,
        string Unit,
        string Color,
        Func<ActivityLog, double?> Read,
        Func<double, string> Format,
        IReadOnlyList<double>? TickSteps = null);
}
