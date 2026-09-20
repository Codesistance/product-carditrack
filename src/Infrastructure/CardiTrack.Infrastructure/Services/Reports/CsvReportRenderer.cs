using System.Globalization;
using System.Text;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Reports;
using CardiTrack.Domain.Extensions;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CsvHelper;

namespace CardiTrack.Infrastructure.Services.Reports;

/// <summary>
/// The raw-data export: one row per member per day, for a caregiver who wants to look at the
/// numbers themselves in a spreadsheet.
/// </summary>
/// <remarks>
/// <para>
/// One flat table rather than a section per data type, because that is what a spreadsheet can
/// actually sort and chart. The daily <see cref="ActivityLog"/> already carries activity, heart
/// rate and sleep together, so the row is the natural grain — a caregiver ticking only "Heart
/// Rate" on M1-17 still gets one row per day, with the columns they did not ask for left out.
/// </para>
/// <para>
/// Alerts are a different grain and so get their own block below the daily table, separated by a
/// blank line — the shape every spreadsheet import handles and no caregiver has to be taught.
/// Alert <em>titles</em> are included because they are generated from our own rule set; alert
/// message bodies and device labels are caregiver free text and stay out
/// (docs/technical/data_protection_architecture.md §70, §85).
/// </para>
/// <para>
/// Every string that came from a person goes through <see cref="WriteText"/>, which stops a
/// spreadsheet reading it as a formula. This file is built to be forwarded — to family, to a
/// clinician — so the usual "it is only opened by the person who exported it" reasoning does not
/// hold here.
/// </para>
/// </remarks>
public class CsvReportRenderer : IReportRenderer
{
    public ReportFormat Format => ReportFormat.Csv;

    public Task<RenderedReport> RenderAsync(
        ReportDataSet data,
        ReportSections sections,
        string? narrative,
        CancellationToken ct = default)
    {
        using var buffer = new StringWriter();
        // Invariant, not the server's culture: a decimal comma would collide with the delimiter
        // and a localised date would be ambiguous in a file the caregiver may send onward.
        using (var csv = new CsvWriter(buffer, CultureInfo.InvariantCulture))
        {
            // A transcript is not a further section of the health export: it has its own grain —
            // one row per message — and the section flags say nothing about it. The PDF is the
            // format a caregiver would usually want a conversation in (see
            // ChatTranscriptDocument); this one is for the caregiver who wants the exchange
            // somewhere they can search, filter or quote from.
            if (data.Transcript is { } transcript)
                WriteTranscript(csv, data, transcript);
            else
                WriteHealthData(csv, data, sections);

            csv.Flush();
        }

        // UTF-8 with a BOM: without it Excel on Windows reads the file as the system codepage and
        // mangles any non-ASCII member name — the one detail that decides whether this file opens
        // correctly for most of the people who will open it. The preamble is prepended explicitly
        // because GetBytes never emits it; only a StreamWriter would.
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        var preamble = encoding.GetPreamble();
        var body = encoding.GetBytes(buffer.ToString());

        var bytes = new byte[preamble.Length + body.Length];
        preamble.CopyTo(bytes, 0);
        body.CopyTo(bytes, preamble.Length);

        return Task.FromResult(new RenderedReport(bytes, "text/csv; charset=utf-8", "csv"));
    }

    private static void WriteHealthData(CsvWriter csv, ReportDataSet data, ReportSections sections)
    {
        if (sections.IncludeMetrics)
            WriteDailyMetrics(csv, data);

        // The frame the daily rows should be read in. A spreadsheet of resting heart rates with
        // nothing beside it leaves whoever opens it — often the person least equipped to answer —
        // deciding for themselves whether 78 is high for this person.
        // On at least one computed row, not on the baseline existing: a member with a baseline
        // but nothing measured in the period produces no rows, and a header with nothing under it
        // tells whoever opens the file that a comparison was available when none was. The PDF
        // already gates on the rows themselves.
        if (sections.IncludeMetrics && ComparisonRows(data).Count > 0)
        {
            csv.NextRecord();
            WriteComparison(csv, data);
        }

        if (sections.IncludeAlerts && data.Members.Any(m => m.Alerts.Count > 0))
        {
            if (sections.IncludeMetrics)
                csv.NextRecord();

            WriteAlerts(csv, data);
        }

        if (sections.IncludeDevices && data.Members.Any(m => m.Devices.Count > 0))
        {
            csv.NextRecord();
            WriteDevices(csv, data);
        }

        if (sections.IncludeJournals && data.Members.Any(m => m.Journals.Count > 0))
        {
            csv.NextRecord();
            WriteJournals(csv, data);
        }

        if (sections.IncludeNotices && data.Members.Any(m => m.Notices.Count > 0))
        {
            csv.NextRecord();
            WriteNotices(csv, data);
        }
    }

    /// <summary>
    /// The conversation as two blocks: one row per message, then the readings behind the replies
    /// — the same series the PDF draws, one row per point, so the figures an answer rests on can
    /// be checked rather than taken on trust.
    /// </summary>
    /// <remarks>
    /// Message text goes through <see cref="WriteText"/> like every other string a person wrote:
    /// a caregiver's question is free text, and an assistant reply quotes it back. The charts are
    /// the ones stored with each reply, not a fresh read of the same days — an exported answer
    /// must not be checkable only against readings that arrived after it was written.
    /// </remarks>
    private static void WriteTranscript(CsvWriter csv, ReportDataSet data, ChatTranscript transcript)
    {
        var memberName = data.Members.Count > 0 ? data.Members[0].Member.Name : string.Empty;

        foreach (var header in new[] { "Member", "SentUtc", "Speaker", "Message" })
            csv.WriteField(header);
        csv.NextRecord();

        // Numbered from one, not from the turn's row id: the number is for a reader citing "the
        // third question", and an id would be an internal handle in a file meant to be forwarded.
        foreach (var turn in transcript.Turns)
        {
            WriteText(csv, memberName);
            csv.WriteField(turn.CreatedAtUtc.UtcDateTime.ToString(
                "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            csv.WriteField(turn.Role == ChatTurnRole.User ? "Caregiver" : "CardiTrack assistant");
            WriteText(csv, turn.Content);
            csv.NextRecord();
        }

        if (!transcript.Turns.Any(t => t.Charts.Count > 0))
            return;

        csv.NextRecord();
        foreach (var header in new[]
                 {
                     "Member", "ReplySentUtc", "Metric", "Date", "Value", "TheirUsual",
                     "TypicalLow", "TypicalHigh", "TypicalSource"
                 })
        {
            csv.WriteField(header);
        }
        csv.NextRecord();

        foreach (var turn in transcript.Turns)
        {
            var sent = turn.CreatedAtUtc.UtcDateTime.ToString(
                "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

            foreach (var series in turn.Charts)
            {
                foreach (var point in series.Points)
                {
                    WriteText(csv, memberName);
                    csv.WriteField(sent);
                    WriteText(csv, series.Metric);
                    csv.WriteField(point.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                    csv.WriteField(point.Value);
                    csv.WriteField(series.Baseline);
                    csv.WriteField(series.Reference?.Low);
                    csv.WriteField(series.Reference?.High);
                    WriteText(csv, series.Reference?.Source);
                    csv.NextRecord();
                }
            }
        }
    }

    /// <summary>
    /// Characters that make a spreadsheet treat a cell as a formula rather than as text.
    /// </summary>
    /// <remarks>
    /// Excel and Sheets both evaluate a cell opening with one of these, so a member named
    /// <c>=HYPERLINK("http://…","Click")</c> would arrive at a clinician's desk as a live link
    /// rather than as a name (CWE-1236). Every string that came from a person is written through
    /// <see cref="WriteText"/>; the numeric columns are not, so a negative distance stays a number.
    /// </remarks>
    private static readonly char[] FormulaTriggers = ['=', '+', '-', '@', '\t', '\r'];

    /// <summary>
    /// Writes a caregiver- or rule-supplied string, neutralising it as a formula if it would
    /// otherwise become one. The leading apostrophe is the convention both Excel and Sheets read
    /// as "this is text", and it is added only to a value that needs it — an ordinary name is
    /// written exactly as it was entered.
    /// </summary>
    private static void WriteText(CsvWriter csv, string? value)
    {
        if (!string.IsNullOrEmpty(value) && Array.IndexOf(FormulaTriggers, value[0]) >= 0)
        {
            csv.WriteField("'" + value);
            return;
        }

        csv.WriteField(value);
    }

    private static void WriteDailyMetrics(CsvWriter csv, ReportDataSet data)
    {
        foreach (var header in new[]
                 {
                     "Member", "Date", "Steps", "DistanceKm", "ActiveMinutes",
                     "RestingHeartRate", "AvgHeartRate", "MinHeartRate", "MaxHeartRate",
                     "SleepMinutes", "SleepEfficiencyPercent", "DeepSleepMinutes",
                     "RemSleepMinutes", "SpO2AveragePercent", "DataSource"
                 })
        {
            csv.WriteField(header);
        }
        csv.NextRecord();

        foreach (var member in data.Members)
        {
            foreach (var log in member.ActivityLogs)
            {
                WriteText(csv, member.Member.Name);
                csv.WriteField(log.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                csv.WriteField(log.Steps);
                csv.WriteField(log.Distance);
                csv.WriteField(log.ActiveMinutes);
                csv.WriteField(log.RestingHeartRate);
                csv.WriteField(log.AvgHeartRate);
                csv.WriteField(log.MinHeartRate);
                csv.WriteField(log.MaxHeartRate);
                csv.WriteField(log.SleepMinutes);
                csv.WriteField(log.SleepEfficiency);
                csv.WriteField(log.DeepSleepMinutes);
                csv.WriteField(log.RemSleepMinutes);
                csv.WriteField(log.SpO2Average);
                csv.WriteField(log.DataSource.ToString());
                csv.NextRecord();
            }
        }
    }

    /// <summary>
    /// One row per metric per member: the period's average against their own learned usual and
    /// the published range, with the publishing body named in its own column.
    /// </summary>
    /// <remarks>
    /// The same computed rows the PDF prints and the narrative is grounded on
    /// (<see cref="ReportComparison"/>), so a caregiver who exports both formats cannot be shown
    /// two different comparisons of the same fortnight. The band's source is a column rather than
    /// a note, because an unattributed range in a spreadsheet reads as ours.
    /// </remarks>
    /// <summary>
    /// Every member's comparison rows, computed once so the decision to write the section and the
    /// section's contents cannot disagree about whether there is anything to say.
    /// </summary>
    private static List<(string Member, ReportComparisonRow Row)> ComparisonRows(ReportDataSet data) =>
        data.Members
            .SelectMany(member => ReportComparison
                .For(
                    member with { ActivityLogs = data.PeriodReadings(member) },
                    member.Member.DateOfBirth.ToAgeInYears(data.To))
                .Select(row => (member.Member.Name, row)))
            .ToList();

    private static void WriteComparison(CsvWriter csv, ReportDataSet data)
    {
        foreach (var header in new[]
                 {
                     "Member", "Metric", "Unit", "PeriodAverage", "TheirUsual", "ChangePercent",
                     "PublishedLow", "PublishedHigh", "PublishedSource", "MeasuredDays",
                 })
        {
            csv.WriteField(header);
        }

        csv.NextRecord();

        foreach (var (memberName, row) in ComparisonRows(data))
        {
            WriteText(csv, memberName);
            WriteText(csv, row.Metric);
            WriteText(csv, row.Unit);
            csv.WriteField(row.PeriodAverage.ToString(CultureInfo.InvariantCulture));
            csv.WriteField(row.Usual?.ToString(CultureInfo.InvariantCulture));
            csv.WriteField(row.ChangePercent?.ToString(CultureInfo.InvariantCulture));
            csv.WriteField(row.BandLow?.ToString(CultureInfo.InvariantCulture));
            csv.WriteField(row.BandHigh?.ToString(CultureInfo.InvariantCulture));
            WriteText(csv, row.BandSource);
            csv.WriteField(row.MeasuredDays.ToString(CultureInfo.InvariantCulture));
            csv.NextRecord();
        }
    }

    private static void WriteAlerts(CsvWriter csv, ReportDataSet data)
    {
        foreach (var header in new[] { "Member", "TriggeredUtc", "Severity", "Type", "Title", "AcknowledgedUtc" })
            csv.WriteField(header);
        csv.NextRecord();

        foreach (var member in data.Members)
        {
            foreach (var alert in member.Alerts)
            {
                WriteText(csv, member.Member.Name);
                csv.WriteField(alert.TriggeredDate.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                csv.WriteField(alert.Severity.ToString());
                csv.WriteField(alert.AlertType.ToString());
                WriteText(csv, alert.Title);
                csv.WriteField(alert.AcknowledgedDate?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                csv.NextRecord();
            }
        }
    }

    /// <summary>
    /// Provenance, not inventory: which kind of device produced the readings and when it last
    /// synced. The caregiver's own label for it ("Mom's Fitbit") is identifier-bearing free text
    /// and never leaves in an export.
    /// </summary>
    private static void WriteDevices(CsvWriter csv, ReportDataSet data)
    {
        foreach (var header in new[] { "Member", "DeviceType", "ConnectionStatus", "ConnectedUtc", "LastSyncUtc" })
            csv.WriteField(header);
        csv.NextRecord();

        foreach (var member in data.Members)
        {
            foreach (var device in member.Devices)
            {
                WriteText(csv, member.Member.Name);
                csv.WriteField(device.DeviceType.ToString());
                csv.WriteField(device.ConnectionStatus.ToString());
                csv.WriteField(device.ConnectedDate?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                csv.WriteField(device.LastSyncDate?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                csv.NextRecord();
            }
        }
    }

    private static void WriteJournals(CsvWriter csv, ReportDataSet data)
    {
        foreach (var header in new[] { "Member", "Book", "LocalDate", "Headline", "Text", "Urgency", "Provenance" })
            csv.WriteField(header);
        csv.NextRecord();

        foreach (var member in data.Members)
        {
            foreach (var entry in member.Journals)
            {
                WriteText(csv, member.Member.Name);
                csv.WriteField(entry.Audience.ToString());
                csv.WriteField(entry.LocalDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                WriteText(csv, entry.Headline);
                WriteText(csv, entry.Text);
                csv.WriteField(entry.Urgency?.ToString());
                csv.WriteField("CardiTrack AI");
                csv.NextRecord();
            }
        }
    }

    private static void WriteNotices(CsvWriter csv, ReportDataSet data)
    {
        foreach (var header in new[] { "Member", "FirstDetectedUtc", "Category", "RuleCode", "State" })
            csv.WriteField(header);
        csv.NextRecord();

        foreach (var member in data.Members)
        {
            foreach (var notice in member.Notices)
            {
                WriteText(csv, member.Member.Name);
                csv.WriteField(notice.FirstDetectedDate.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                csv.WriteField(notice.Category.ToString());
                csv.WriteField(notice.RuleCode);
                csv.WriteField(notice.State.ToString());
                csv.NextRecord();
            }
        }
    }
}
