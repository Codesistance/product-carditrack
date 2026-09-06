using System.Globalization;
using System.Text;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Reports;
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
            if (sections.IncludeMetrics)
                WriteDailyMetrics(csv, data);

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
}
