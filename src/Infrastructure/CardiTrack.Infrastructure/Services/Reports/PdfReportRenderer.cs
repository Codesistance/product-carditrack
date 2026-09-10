using System.Globalization;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Reports;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
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
/// questioned; then alerts. Every page is dated and numbered, and every page carries the
/// confidentiality footer, since printed pages get separated.
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
    // The app's own palette (Colors.xaml), so the printout and the screen agree.
    private const string Ink = "#1F1F1F";
    private const string Body = "#343434";
    private const string Secondary = "#727272";
    private const string Muted = "#939DAA";
    private const string Divider = "#E2E8F0";
    private const string Brand = "#1884DC";
    private const string BrandDark = "#174E86";
    private const string Tint = "#F4F8FB";
    private const string TableHead = "#F2F5F9";
    private const string Zebra = "#FAFBFD";
    private const string White = "#FFFFFF";

    private const float MarginHorizontalCm = 1.8f;
    private const float MarginVerticalCm = 1.4f;

    /// <summary>The content width, and so the chart width: the figure is drawn 1:1 in points.</summary>
    private static readonly float ContentWidth =
        PageSizes.A4.Width - 2 * MarginHorizontalCm * 72 / 2.54f;

    private const float ChartHeight = 130;

    private static readonly Metric[] Metrics =
    [
        new("Steps", "steps a day", "#1884DC", log => log.Steps, v => v.ToString("N0", CultureInfo.InvariantCulture)),
        new("Resting heart rate", "bpm", "#E53E3E", log => log.RestingHeartRate, v => v.ToString("0", CultureInfo.InvariantCulture)),
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
            Compose(data, sections, narrative).GeneratePdf(), "application/pdf", "pdf"));

    /// <summary>The document before it is serialised — what a preview or a test renders pages from.</summary>
    internal static IDocument Compose(ReportDataSet data, ReportSections sections, string? narrative) =>
        Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.MarginHorizontal(MarginHorizontalCm, Unit.Centimetre);
                page.MarginVertical(MarginVerticalCm, Unit.Centimetre);
                page.DefaultTextStyle(t => t.FontSize(10).FontColor(Body).LineHeight(1.3f));

                page.Header().Element(h => ComposeHeader(h, data));
                page.Content().Element(c => ComposeContent(c, data, sections, narrative));
                page.Footer().Element(ComposeFooter);
            });
        });

    // ── Chrome ──────────────────────────────────────────────────────────────────

    private static void ComposeHeader(IContainer container, ReportDataSet data) =>
        container.PaddingBottom(14).Column(column =>
        {
            column.Item().BorderBottom(1.5f).BorderColor(Brand).PaddingBottom(6).Row(row =>
            {
                row.RelativeItem().Text(text =>
                {
                    text.Span("CardiTrack").FontSize(12).Bold().FontColor(BrandDark);
                    text.Span("   Health export").FontSize(9).FontColor(Secondary);
                });

                row.RelativeItem().AlignRight().AlignBottom().Text(
                        $"{data.From:d MMM yyyy} – {data.To:d MMM yyyy}")
                    .FontSize(9).FontColor(Secondary);
            });
        });

    private static void ComposeFooter(IContainer container) =>
        container.PaddingTop(8).BorderTop(1).BorderColor(Divider).PaddingTop(5).Row(row =>
        {
            // Printed pages get separated from each other, so the warning belongs on each one
            // rather than on a cover sheet.
            row.RelativeItem().Text(
                    "Confidential health information · CardiTrack · Not a clinical assessment")
                .FontSize(7.5f).FontColor(Muted);

            row.ConstantItem(80).AlignRight().Text(text =>
            {
                text.DefaultTextStyle(t => t.FontSize(7.5f).FontColor(Muted));
                text.Span("Page ");
                text.CurrentPageNumber();
                text.Span(" of ");
                text.TotalPages();
            });
        });

    // ── Content ─────────────────────────────────────────────────────────────────

    private static void ComposeContent(
        IContainer container, ReportDataSet data, ReportSections sections, string? narrative)
    {
        container.Column(column =>
        {
            column.Spacing(0);

            column.Item().Element(t => TitleBlock(t, data));

            foreach (var member in data.Members)
            {
                column.Item().PaddingTop(18).Element(m => MemberBanner(m, member, sections));

                if (sections.IncludeMetrics && member.ActivityLogs.Count > 0)
                    column.Item().PaddingTop(12).Element(k => KeyFigures(k, member));
            }

            if (!string.IsNullOrWhiteSpace(narrative))
                column.Item().PaddingTop(22).Element(s => Summary(s, narrative));

            foreach (var member in data.Members)
            {
                var prefix = data.Members.Count > 1 ? $"{member.Member.Name} · " : string.Empty;

                if (sections.IncludeMetrics)
                {
                    if (sections.IncludeTrends && member.ActivityLogs.Count > 0)
                        Section(column, prefix + "Trends", e => Charts(e, member, data.From, data.To));

                    if (member.ActivityLogs.Count > 0)
                        Section(column, prefix + "Daily readings", e => DailyTable(e, member));
                    else
                        Section(column, prefix + "Daily readings", e => EmptyState(e, "No readings were recorded in this period."));
                }

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

    private static void TitleBlock(IContainer container, ReportDataSet data) =>
        container.Column(column =>
        {
            column.Item().Text(data.Title ?? "Health export")
                .FontSize(22).Bold().FontColor(Ink).LineHeight(1.1f);

            var days = data.To.DayNumber - data.From.DayNumber + 1;
            column.Item().PaddingTop(4).Text(
                    $"{data.From:d MMMM yyyy} to {data.To:d MMMM yyyy}  ·  {days} days  ·  "
                    + $"Prepared {DateTime.UtcNow:d MMMM yyyy}")
                .FontSize(10).FontColor(Secondary);
        });

    /// <summary>
    /// Who the document is about, and where the numbers came from — device types only, never the
    /// caregiver's label for a device (docs/technical/data_protection_architecture.md §70).
    /// </summary>
    private static void MemberBanner(IContainer container, ReportMemberData member, ReportSections sections)
    {
        var age = AgeAt(member.Member.DateOfBirth, DateOnly.FromDateTime(DateTime.UtcNow));
        var facts = new List<string> { $"Age {age}", SexLabel(member.Member.Gender) };

        if (sections.IncludeDevices && member.Devices.Count > 0)
        {
            var types = member.Devices
                .Select(d => d.DeviceType.ToString())
                .Distinct()
                .OrderBy(t => t, StringComparer.Ordinal);
            facts.Add("Source: " + string.Join(", ", types));
        }

        var measured = member.ActivityLogs.Count(HasAnyReading);
        facts.Add(measured == 1 ? "1 day with readings" : $"{measured} days with readings");

        container
            .Background(Tint).CornerRadius(6)
            .BorderLeft(3).BorderColor(Brand)
            .PaddingVertical(9).PaddingHorizontal(12)
            .Column(column =>
            {
                column.Item().Text(member.Member.Name).FontSize(14).SemiBold().FontColor(Ink);
                column.Item().PaddingTop(2).Text(string.Join("   ·   ", facts))
                    .FontSize(9).FontColor(Secondary);
            });
    }

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

    private static void StatTile(IContainer container, Metric metric, IReadOnlyList<double> values) =>
        container
            .Border(1).BorderColor(Divider).CornerRadius(6)
            .PaddingVertical(8).PaddingHorizontal(10)
            .Column(column =>
            {
                column.Item().Row(row =>
                {
                    row.ConstantItem(8).AlignMiddle().Height(8).Width(8)
                        .Background(metric.Color).CornerRadius(4);
                    row.RelativeItem().PaddingLeft(6).Text(metric.Title)
                        .FontSize(8.5f).FontColor(Secondary);
                });

                if (values.Count == 0)
                {
                    column.Item().PaddingTop(3).Text("—").FontSize(17).SemiBold().FontColor(Muted);
                    column.Item().Text("not measured").FontSize(7.5f).FontColor(Muted);
                    return;
                }

                column.Item().PaddingTop(3).Text(text =>
                {
                    text.Span(metric.Format(values.Average())).FontSize(17).SemiBold().FontColor(Ink);
                    text.Span("  " + metric.Unit).FontSize(8).FontColor(Secondary);
                });

                column.Item().Text(
                        $"{metric.Format(values.Min())} – {metric.Format(values.Max())}  ·  {values.Count} d")
                    .FontSize(7.5f).FontColor(Muted);
            });

    private static void SectionTitle(IContainer container, string title) =>
        container
            .BorderBottom(1).BorderColor(Divider)
            .PaddingBottom(4)
            .Text(title).FontSize(13).SemiBold().FontColor(Ink);

    private static void EmptyState(IContainer container, string message) =>
        container.PaddingTop(8).Text(message).FontSize(9.5f).Italic().FontColor(Secondary);

    // ── Narrative ───────────────────────────────────────────────────────────────

    private static void Summary(IContainer container, string narrative) =>
        container.Column(column =>
        {
            column.Item().Element(e => SectionTitle(e, "Summary"));
            column.Item().PaddingTop(8).Element(e => Markdown(e, narrative));

            // Attribution sits with the text it qualifies, not in a footnote a reader skips.
            column.Item().PaddingTop(10).Element(AiAttribution);
        });

    private static void AiAttribution(IContainer container) =>
        container
            .Background(Tint).CornerRadius(4)
            .PaddingVertical(5).PaddingHorizontal(8)
            .Row(row =>
            {
                row.AutoItem().AlignMiddle()
                    .Background(BrandDark).CornerRadius(3).PaddingVertical(1).PaddingHorizontal(4)
                    .Text("AI").FontSize(6.5f).Bold().FontColor(Colors.White);
                row.RelativeItem().PaddingLeft(7).AlignMiddle().Text(
                        "Written by CardiTrack's AI assistant from the readings in this document. "
                        + "It is not a clinical assessment.")
                    .FontSize(8).Italic().FontColor(Secondary);
            });

    /// <summary>The narrative's Markdown, laid out as formatting rather than printed as marks.</summary>
    private static void Markdown(IContainer container, string markdown) =>
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
                            text.DefaultTextStyle(t => t.FontSize(heading.Level <= 2 ? 11.5f : 10.5f).SemiBold().FontColor(Ink));
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
                                        .Text(marker).FontColor(Secondary);
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

    // ── Charts ──────────────────────────────────────────────────────────────────

    private static void Charts(IContainer container, ReportMemberData member, DateOnly from, DateOnly to) =>
        container.Column(column =>
        {
            column.Spacing(10);
            foreach (var metric in Metrics)
            {
                var chart = ReportChartRenderer.Line(
                    member.ActivityLogs, metric.Read, from, to, metric.Color, metric.Format,
                    ContentWidth, ChartHeight, metric.TickSteps);
                if (chart is null)
                    continue;

                var values = member.ActivityLogs.Select(metric.Read).Where(v => v is not null).Select(v => v!.Value).ToList();

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
                            .FontSize(8.5f).FontColor(Muted);
                    });
                    block.Item().PaddingTop(4).Element(e => Figure(e, chart));
                });
            }
        });

    /// <summary>
    /// The marks as vector geometry with the labels set over them in the document's own face —
    /// see <see cref="ReportChartRenderer"/> for why the SVG carries no text of its own.
    /// </summary>
    private static void Figure(IContainer container, ReportChartRenderer.Chart chart) =>
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
    /// Severity as a coloured dot beside its word — never the colour alone, which a photocopier
    /// and a colour-blind reader both lose.
    /// </summary>
    private static void SeverityChip(IContainer container, AlertSeverity severity) =>
        container.AlignLeft().Row(row =>
        {
            row.ConstantItem(7).AlignMiddle().Height(7).Width(7)
                .Background(SeverityColor(severity)).CornerRadius(3.5f);
            row.AutoItem().PaddingLeft(5).AlignMiddle().Text(severity.ToString()).FontSize(9);
        });

    private static string SeverityColor(AlertSeverity severity) => severity switch
    {
        AlertSeverity.Red => "#E53E3E",
        AlertSeverity.Orange => "#ED7B2F",
        AlertSeverity.Yellow => "#F0A92E",
        AlertSeverity.Green => "#36C09B",
        _ => Muted
    };

    private static void Journals(IContainer container, ReportMemberData member) =>
        container.PaddingTop(8).Column(column =>
        {
            column.Spacing(10);
            foreach (var entry in member.Journals.OrderBy(j => j.LocalDate))
            {
                column.Item().ShowEntire()
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

                        card.Item().PaddingTop(4).Element(e => Markdown(e, entry.Text));
                        card.Item().PaddingTop(6).Text(
                                "Written by CardiTrack's AI assistant. Not a clinical assessment.")
                            .FontSize(7.5f).Italic().FontColor(Muted);
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
                BodyCell(table.Cell(), notice.Category.ToString(), shade, right: false);
                BodyCell(table.Cell(), NoticeLabel(notice.RuleCode), shade, right: false);
                BodyCell(table.Cell(), notice.State.ToString(), shade, right: false);
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
        (right ? box.AlignRight() : box).Text(text).FontSize(9).FontColor(quiet ? Muted : Body);
    }

    // ── Words and figures ───────────────────────────────────────────────────────

    private static string BookName(DigestAudience audience) => audience switch
    {
        DigestAudience.Weekbook => "Weekbook",
        DigestAudience.Monthbook => "Monthbook",
        _ => "Daybook"
    };

    /// <summary>
    /// Rule codes are catalogue keys (<c>DEVICE_STALE_LONG</c>), not caregiver free text.
    /// Rendered as words so the table is readable without becoming a localization dump.
    /// </summary>
    private static string NoticeLabel(string ruleCode)
    {
        var words = ruleCode.Replace("_", " ", StringComparison.Ordinal).ToLowerInvariant();
        return words.Length == 0 ? words : char.ToUpperInvariant(words[0]) + words[1..];
    }

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
    /// <param name="TickSteps">Axis steps in the metric's unit, where the 1–2–5 sequence would
    /// read oddly; null for the default.</param>
    private sealed record Metric(
        string Title,
        string Unit,
        string Color,
        Func<ActivityLog, double?> Read,
        Func<double, string> Format,
        IReadOnlyList<double>? TickSteps = null);
}
