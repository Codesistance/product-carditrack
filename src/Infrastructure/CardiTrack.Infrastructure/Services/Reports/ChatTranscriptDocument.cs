using System.Globalization;
using CardiTrack.Application.DTOs.Common;
using CardiTrack.Application.Reports;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace CardiTrack.Infrastructure.Services.Reports;

/// <summary>
/// A copy of one chat conversation: what the caregiver asked, what the assistant answered, and
/// the charts each answer was read against.
/// </summary>
/// <remarks>
/// <para>
/// Laid out as a record of an exchange rather than as a screenshot of a chat sheet. Chat bubbles
/// alternating left and right are a screen idiom that survives printing badly — half the page
/// width goes unused and the reading order stops being obvious the moment a turn breaks across
/// pages. Each turn is a labelled block instead, in the order it happened, with the time it
/// happened beside the label.
/// </para>
/// <para>
/// The charts are the ones stored on the reply, not a fresh plot of the same days: the figures a
/// clinician is being shown must be the figures the answer above them was written from. Drawn
/// with the same renderer, comparisons and identity colours as the health-data export and the
/// app — see <see cref="ReportChartRenderer.Series"/> and <see cref="ChatChartStyle"/>.
/// </para>
/// <para>
/// The provenance banner is not decoration. This document quotes an AI assistant answering
/// questions about a named person, and it is built to be forwarded — to family, to a clinician.
/// Anyone reading it has to be able to tell at a glance what wrote the answers and what they are
/// not.
/// </para>
/// </remarks>
internal static class ChatTranscriptDocument
{
    private const float MarginHorizontalCm = 1.8f;
    private const float MarginVerticalCm = 1.4f;

    private const float ChartHeight = 120;

    /// <summary>Charts sit inside the answer block, which is indented from the page edge.</summary>
    private const float AnswerIndent = 12;

    private static readonly float ChartWidth = PdfReportRenderer.ContentWidth - AnswerIndent;

    internal static IDocument Compose(ReportDataSet data, ChatTranscript transcript) =>
        Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.MarginHorizontal(MarginHorizontalCm, Unit.Centimetre);
                page.MarginVertical(MarginVerticalCm, Unit.Centimetre);
                page.DefaultTextStyle(t => t
                    .FontFamily(ReportFonts.Families)
                    .FontSize(10).FontColor(ReportPalette.Body).LineHeight(1.35f));

                page.Header().Element(h => Header(h, transcript));
                page.Content().Element(c => Content(c, data, transcript));
                page.Footer().Element(Footer);
            });
        });

    // ── Chrome ──────────────────────────────────────────────────────────────────

    private static void Header(IContainer container, ChatTranscript transcript) =>
        container.PaddingBottom(14).Column(column =>
        {
            column.Item().BorderBottom(1.5f).BorderColor(ReportPalette.Brand).PaddingBottom(6).Row(row =>
            {
                row.RelativeItem().Text(text =>
                {
                    text.Span("CardiTrack").FontSize(12).Bold().FontColor(ReportPalette.BrandDark);
                    text.Span("   Chat transcript").FontSize(9).FontColor(ReportPalette.Secondary);
                });

                row.RelativeItem().AlignRight().AlignBottom()
                    .Text(Day(transcript.StartedAtUtc))
                    .FontSize(9).FontColor(ReportPalette.Secondary);
            });
        });

    private static void Footer(IContainer container) =>
        container.PaddingTop(8).BorderTop(1).BorderColor(ReportPalette.Divider).PaddingTop(5).Row(row =>
        {
            row.RelativeItem().Text(
                    "Confidential health information · CardiTrack · AI-generated answers · Not a clinical assessment")
                .FontSize(7.5f).FontColor(ReportPalette.Muted);

            row.ConstantItem(80).AlignRight().Text(text =>
            {
                text.DefaultTextStyle(t => t.FontSize(7.5f).FontColor(ReportPalette.Muted));
                text.Span("Page ");
                text.CurrentPageNumber();
                text.Span(" of ");
                text.TotalPages();
            });
        });

    // ── Content ─────────────────────────────────────────────────────────────────

    private static void Content(IContainer container, ReportDataSet data, ChatTranscript transcript) =>
        container.Column(column =>
        {
            column.Spacing(0);

            column.Item().Element(t => TitleBlock(t, data, transcript));
            column.Item().PaddingTop(14).Element(Provenance);

            if (transcript.Turns.Count == 0)
            {
                column.Item().PaddingTop(20).Element(e => EmptyState(
                    e, "This conversation has no messages in it."));
                return;
            }

            foreach (var turn in transcript.Turns)
                column.Item().PaddingTop(16).Element(e => Turn(e, turn));
        });

    private static void TitleBlock(IContainer container, ReportDataSet data, ChatTranscript transcript) =>
        container.Column(column =>
        {
            column.Item().Text(data.Title ?? transcript.Label)
                .FontSize(17).Bold().FontColor(ReportPalette.Ink).LineHeight(1.2f);

            var member = data.Members.Count > 0 ? data.Members[0].Member : null;
            if (member is not null)
            {
                column.Item().PaddingTop(3).Text(Subject(member, DateOnly.FromDateTime(transcript.StartedAtUtc.UtcDateTime)))
                    .FontSize(10).FontColor(ReportPalette.Secondary);
            }

            column.Item().PaddingTop(6).Text(Summary(transcript))
                .FontSize(8.5f).FontColor(ReportPalette.Muted);
        });

    /// <summary>
    /// Who wrote the answers below, in the one place a reader looks first. The wording is
    /// deliberately plain: a document handed to a clinician must not leave them guessing which
    /// sentences a model wrote.
    /// </summary>
    private static void Provenance(IContainer container) =>
        container.Background(ReportPalette.Tint).Padding(10).Column(column =>
        {
            column.Item().Text("About this transcript")
                .FontSize(9).SemiBold().FontColor(ReportPalette.BrandDark);
            column.Item().PaddingTop(3).Text(
                    "The replies here were written by CardiTrack's assistant from this person's own "
                    + "recorded readings. They are not a clinical assessment and were not reviewed by a "
                    + "clinician. The charts are the ones each reply was based on, as they stood when it "
                    + "was written.")
                .FontSize(8.5f).FontColor(ReportPalette.Secondary);
        });

    // ── Turns ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// One question or one answer. The label and the first lines under it stay together: a
    /// "Question" heading alone at the foot of a page, with the question overleaf, is how a
    /// generated document announces that nothing laid it out.
    /// </summary>
    private static void Turn(IContainer container, ChatTranscriptTurn turn) =>
        container.EnsureSpace(64).Column(column =>
        {
            var asked = turn.Role == ChatTurnRole.User;

            column.Item().Row(row =>
            {
                row.RelativeItem().Text(asked ? "Question" : "CardiTrack assistant")
                    .FontSize(8.5f).SemiBold()
                    .FontColor(asked ? ReportPalette.BrandDark : ReportPalette.Secondary);
                row.RelativeItem().AlignRight().Text(Time(turn.CreatedAtUtc))
                    .FontSize(8).FontColor(ReportPalette.Muted);
            });

            // The question is set on a tinted rule and the answer is indented under it, so the
            // thread reads as a sequence of exchanges without the two ever being confusable.
            if (asked)
            {
                column.Item().PaddingTop(3).BorderLeft(2).BorderColor(ReportPalette.Brand)
                    .PaddingLeft(8).Element(e => Prose(e, turn.Content, ReportPalette.Ink, semiBold: true));
                return;
            }

            column.Item().PaddingTop(3).PaddingLeft(AnswerIndent)
                .Element(e => Prose(e, turn.Content, ReportPalette.Body, semiBold: false));

            if (turn.Charts.Count == 0)
                return;

            column.Item().PaddingLeft(AnswerIndent).PaddingTop(8).Element(e => Charts(e, turn.Charts));
        });

    /// <summary>
    /// The turn's text, paragraph by paragraph. A reply that came back empty — an unreadable row
    /// under a rotated key, the fallback <c>ChatTranscriptSource</c> returns rather than failing
    /// the export — says so rather than leaving a gap the reader has to interpret.
    /// </summary>
    private static void Prose(IContainer container, string content, string color, bool semiBold) =>
        container.Column(column =>
        {
            var paragraphs = content
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim())
                .Where(p => p.Length > 0)
                .ToList();

            if (paragraphs.Count == 0)
            {
                column.Item().Text("[This message could not be read.]")
                    .FontSize(9.5f).Italic().FontColor(ReportPalette.Muted);
                return;
            }

            column.Spacing(4);
            foreach (var paragraph in paragraphs)
            {
                var text = column.Item().Text(paragraph).FontSize(10).FontColor(color);
                if (semiBold)
                    text.SemiBold();
            }
        });

    // ── Charts ──────────────────────────────────────────────────────────────────

    private static void Charts(IContainer container, IReadOnlyList<ChartSeries> charts) =>
        container.Column(column =>
        {
            column.Spacing(10);

            foreach (var series in charts)
            {
                var style = ChatChartStyle.For(series.Metric);
                var chart = ReportChartRenderer.Series(
                    series, style.Color, style.Format, ChartWidth, ChartHeight, style.TickSteps);
                if (chart is null)
                    continue;

                // A chart split across a page break is two half-charts; keep each one whole.
                column.Item().ShowEntire().Column(block =>
                {
                    block.Item().Row(row =>
                    {
                        row.ConstantItem(8).AlignMiddle().Height(8).Width(8)
                            .Background(style.Color).CornerRadius(4);
                        row.RelativeItem().PaddingLeft(6).Text(text =>
                        {
                            text.Span(series.Metric).FontSize(10).SemiBold().FontColor(ReportPalette.Ink);
                            if (style.Unit.Length > 0)
                                text.Span("  " + style.Unit).FontSize(8.5f).FontColor(ReportPalette.Secondary);
                        });
                        row.RelativeItem().AlignRight().Text(Span(series, style))
                            .FontSize(8.5f).FontColor(ReportPalette.Muted);
                    });

                    // The comparisons the reply carried, in words as well as in marks: the dashed
                    // rule and the shaded band are unlabelled inside the figure, and a band drawn
                    // without its source attributed would be a range this product appeared to be
                    // publishing itself.
                    if (Comparisons(series, style) is { Length: > 0 } legend)
                        block.Item().PaddingTop(1).Text(legend).FontSize(8).FontColor(ReportPalette.Muted);

                    block.Item().PaddingTop(4).Element(e => PdfReportRenderer.Figure(e, chart));
                });
            }
        });

    private static string Span(ChartSeries series, ChatChartStyle style)
    {
        var first = series.Points.Min(p => p.Date);
        var last = series.Points.Max(p => p.Date);
        var days = $"{Day(first)} – {Day(last)}";
        return first == last
            ? $"{style.Format(series.Points[0].Value)}  ·  {Day(first)}"
            : $"{series.Points.Count} readings  ·  {days}";
    }

    private static string Comparisons(ChartSeries series, ChatChartStyle style)
    {
        var parts = new List<string>();
        if (series.Baseline is { } baseline)
            parts.Add($"Their usual: {style.Format(baseline)}");
        if (series.Reference is { } reference)
        {
            parts.Add(
                $"Typical {style.Format((double)reference.Low)}–{style.Format((double)reference.High)} "
                + $"({reference.Source})");
        }

        return string.Join("   ·   ", parts);
    }

    // ── Bits ────────────────────────────────────────────────────────────────────

    private static void EmptyState(IContainer container, string message) =>
        container.Background(ReportPalette.Zebra).Padding(10)
            .Text(message).FontSize(9.5f).Italic().FontColor(ReportPalette.Muted);

    /// <summary>
    /// Who the conversation was about. Age rather than the date of birth itself, for the same
    /// reason the health export renders it that way: it is what a reading is judged against, and
    /// one category less identifying in a document that may be photocopied.
    /// </summary>
    private static string Subject(CardiMember member, DateOnly on)
    {
        var age = on.Year - member.DateOfBirth.Year;
        if (on < member.DateOfBirth.AddYears(age))
            age--;

        var sex = member.Gender switch
        {
            Gender.Male => "male",
            Gender.Female => "female",
            _ => null,
        };

        return sex is null
            ? $"About {member.Name} · {age}"
            : $"About {member.Name} · {age} · {sex}";
    }

    private static string Summary(ChatTranscript transcript)
    {
        var questions = transcript.QuestionCount;
        var asked = questions == 1 ? "1 question" : $"{questions} questions";
        return $"{asked} · {Day(transcript.StartedAtUtc)} {Time(transcript.StartedAtUtc)}"
            + $" – {Day(transcript.LastTurnAtUtc)} {Time(transcript.LastTurnAtUtc)}";
    }

    /// <summary>
    /// Times print as UTC, and every one of them says so. The generation has no idea what the
    /// caregiver's clock said, and an unlabelled local-looking time in a document that may be
    /// read in another country is worse than an honest one in a zone.
    /// </summary>
    /// <remarks>
    /// The zone rides on each time rather than being stated once at the top. Four characters per
    /// turn is a cheap price for a document built to be forwarded: a reader who starts halfway
    /// down the second page, or who is handed one page of it, never sees the line that would
    /// have carried the qualifier.
    /// </remarks>
    internal static string Time(DateTimeOffset value) =>
        value.UtcDateTime.ToString("HH:mm", CultureInfo.InvariantCulture) + " UTC";

    private static string Day(DateTimeOffset value) =>
        value.UtcDateTime.ToString("d MMM yyyy", CultureInfo.InvariantCulture);

    private static string Day(DateOnly value) =>
        value.ToString("d MMM", CultureInfo.InvariantCulture);
}
