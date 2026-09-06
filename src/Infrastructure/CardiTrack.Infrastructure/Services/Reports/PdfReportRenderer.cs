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
/// consultation, and a family member filing it. So: the AI narrative first, because it says what
/// happened in plain language; then the daily table, because that is what gets questioned; then
/// alerts. Every page is dated and numbered, and every page carries the confidentiality footer,
/// since printed pages get separated.
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
    public ReportFormat Format => ReportFormat.Pdf;

    public Task<RenderedReport> RenderAsync(
        ReportDataSet data,
        ReportSections sections,
        string? narrative,
        CancellationToken ct = default)
    {
        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(2, Unit.Centimetre);
                page.DefaultTextStyle(t => t.FontSize(10).FontColor(Colors.Grey.Darken4));

                page.Header().Element(h => ComposeHeader(h, data));
                page.Content().Element(c => ComposeContent(c, data, sections, narrative));
                page.Footer().Element(ComposeFooter);
            });
        });

        return Task.FromResult(new RenderedReport(
            document.GeneratePdf(), "application/pdf", "pdf"));
    }

    private static void ComposeHeader(IContainer container, ReportDataSet data)
    {
        container.PaddingBottom(10).Column(column =>
        {
            column.Item().Text(data.Title ?? "CardiTrack Health Export")
                .FontSize(18).SemiBold();

            column.Item().Text(
                    $"{data.From:d MMMM yyyy} – {data.To:d MMMM yyyy}   ·   "
                    + $"Generated {DateTime.UtcNow:d MMMM yyyy} UTC")
                .FontSize(9).FontColor(Colors.Grey.Darken1);
        });
    }

    private static void ComposeContent(
        IContainer container, ReportDataSet data, ReportSections sections, string? narrative)
    {
        container.PaddingVertical(10).Column(column =>
        {
            column.Spacing(14);

            if (!string.IsNullOrWhiteSpace(narrative))
            {
                column.Item().Element(e => Section(e, "Summary"));
                column.Item().Text(narrative.Trim()).LineHeight(1.4f);

                // Attribution sits with the text it qualifies, not in a footnote a reader skips.
                column.Item().Text(
                        "This summary was written by CardiTrack's AI assistant from the readings "
                        + "in this document. It is not a clinical assessment.")
                    .FontSize(8).Italic().FontColor(Colors.Grey.Darken1);
            }

            foreach (var member in data.Members)
            {
                column.Item().Element(e => Section(e, member.Member.Name));
                column.Item().Text(Provenance(member, sections))
                    .FontSize(9).FontColor(Colors.Grey.Darken1);

                if (sections.IncludeMetrics)
                {
                    if (member.ActivityLogs.Count > 0)
                        column.Item().Element(e => DailyTable(e, member));
                    else
                        column.Item().Text("No readings were recorded in this period.")
                            .FontSize(9).Italic();
                }

                if (sections.IncludeAlerts && member.Alerts.Count > 0)
                {
                    column.Item().Text("Alerts").SemiBold();
                    column.Item().Element(e => AlertsTable(e, member));
                }
            }
        });
    }

    private static void Section(IContainer container, string title) =>
        container
            .BorderBottom(1).BorderColor(Colors.Grey.Lighten1)
            .PaddingBottom(3)
            .Text(title).FontSize(13).SemiBold();

    /// <summary>
    /// Where the numbers came from — device types only, never the caregiver's label for a device
    /// (docs/technical/data_protection_architecture.md §70).
    /// </summary>
    private static string Provenance(ReportMemberData member, ReportSections sections)
    {
        var age = AgeAt(member.Member.DateOfBirth, DateOnly.FromDateTime(DateTime.UtcNow));
        var line = $"Age {age}   ·   {member.ActivityLogs.Count} day(s) with readings";

        if (!sections.IncludeDevices || member.Devices.Count == 0)
            return line;

        var types = member.Devices
            .Select(d => d.DeviceType.ToString())
            .Distinct()
            .OrderBy(t => t, StringComparer.Ordinal);

        return line + $"   ·   Source: {string.Join(", ", types)}";
    }

    private static void DailyTable(IContainer container, ReportMemberData member) =>
        container.Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.RelativeColumn(2); // Date
                columns.RelativeColumn();  // Steps
                columns.RelativeColumn();  // Resting HR
                columns.RelativeColumn();  // Sleep
                columns.RelativeColumn();  // SpO2
            });

            table.Header(header =>
            {
                HeaderCell(header.Cell(), "Date");
                HeaderCell(header.Cell(), "Steps");
                HeaderCell(header.Cell(), "Resting HR");
                HeaderCell(header.Cell(), "Sleep");
                HeaderCell(header.Cell(), "SpO₂");
            });

            foreach (var log in member.ActivityLogs)
            {
                BodyCell(table.Cell(), log.Date.ToString("ddd d MMM", CultureInfo.InvariantCulture));
                BodyCell(table.Cell(), Figure(log.Steps));
                BodyCell(table.Cell(), Figure(log.RestingHeartRate, " bpm"));
                BodyCell(table.Cell(), Sleep(log.SleepMinutes));
                BodyCell(table.Cell(), Figure(log.SpO2Average, "%"));
            }
        });

    private static void AlertsTable(IContainer container, ReportMemberData member) =>
        container.Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.RelativeColumn(2);
                columns.RelativeColumn(2);
                columns.RelativeColumn(5);
            });

            table.Header(header =>
            {
                HeaderCell(header.Cell(), "Date");
                HeaderCell(header.Cell(), "Severity");
                HeaderCell(header.Cell(), "Alert");
            });

            foreach (var alert in member.Alerts)
            {
                BodyCell(table.Cell(), alert.TriggeredDate.ToString("d MMM yyyy", CultureInfo.InvariantCulture));
                BodyCell(table.Cell(), alert.Severity.ToString());
                BodyCell(table.Cell(), alert.Title);
            }
        });

    private static void HeaderCell(IContainer cell, string text) =>
        cell.BorderBottom(1).BorderColor(Colors.Grey.Medium).PaddingVertical(4)
            .Text(text).SemiBold().FontSize(9);

    private static void BodyCell(IContainer cell, string text) =>
        cell.BorderBottom(1).BorderColor(Colors.Grey.Lighten2).PaddingVertical(3)
            .Text(text).FontSize(9);

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
        minutes is { } m ? $"{m / 60}h {m % 60:00}m" : "—";

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

    private static void ComposeFooter(IContainer container) =>
        container.Column(column =>
        {
            column.Item().PaddingTop(6).BorderTop(1).BorderColor(Colors.Grey.Lighten1);
            column.Item().PaddingTop(4).Row(row =>
            {
                // Printed pages get separated from each other, so the warning belongs on each one
                // rather than on a cover sheet.
                row.RelativeItem().Text(
                        "Confidential health information — CardiTrack. Not a clinical assessment.")
                    .FontSize(7).FontColor(Colors.Grey.Darken1);

                row.ConstantItem(60).AlignRight().Text(text =>
                {
                    text.DefaultTextStyle(t => t.FontSize(7).FontColor(Colors.Grey.Darken1));
                    text.CurrentPageNumber();
                    text.Span(" / ");
                    text.TotalPages();
                });
            });
        });
}
