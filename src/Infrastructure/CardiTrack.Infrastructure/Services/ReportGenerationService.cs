using System.Text;
using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Interfaces.Clients;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Reports;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CardiTrack.Infrastructure.Services;

/// <summary>
/// Queues, renders and serves health-data exports.
/// </summary>
/// <remarks>
/// <para>
/// State is durable: a <see cref="Report"/> row carries the request and its outcome, and the
/// rendered bytes go to the export bucket. The previous design kept both as distributed-cache
/// entries on a one-hour TTL, which meant a deploy lost in-flight generations *and* finished
/// reports a caregiver had not downloaded yet — for a document someone plans to take to an
/// appointment, that is the wrong failure.
/// </para>
/// <para>
/// Generation still runs on a detached task rather than a queue, but it now opens its own DI
/// scope: the request scope's <see cref="IUnitOfWork"/> is disposed the moment the 202 goes out,
/// so continuing to use it was a race the cache-backed version could lose silently. Anything the
/// detached task fails to finish is failed out by <c>ExpiredReportCleanupWorker</c> rather than
/// left pending forever.
/// </para>
/// </remarks>
public class ReportGenerationService : IReportGenerationService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IReportStorage _storage;
    private readonly ICardiMemberAccessService _access;
    private readonly ReportStorageOptions _options;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ReportGenerationService> _logger;

    public ReportGenerationService(
        IUnitOfWork unitOfWork,
        IReportStorage storage,
        ICardiMemberAccessService access,
        ReportStorageOptions options,
        IServiceScopeFactory scopeFactory,
        ILogger<ReportGenerationService> logger)
    {
        _unitOfWork = unitOfWork;
        _storage = storage;
        _access = access;
        _options = options;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<ReportQueuedResponse> GenerateAsync(Guid requestingUserId, GenerateReportRequest request)
    {
        // Checked here, before anything is queued, so an unauthorised request fails as a 404 on
        // the call rather than as a silently-abandoned background job. Because the whole set is
        // vetted up front, the gather below can trust every id in the request.
        await _access.RequireViewAccessAsync(requestingUserId, request.CardiMemberIds);

        var now = DateTime.UtcNow;
        var report = new Report
        {
            OwnerUserId = requestingUserId,
            CardiMemberIds = request.CardiMemberIds.ToList(),
            Format = request.Format,
            Status = ReportStatus.Pending,
            DateRangeFrom = request.DateRangeFrom,
            DateRangeTo = request.DateRangeTo,
            Title = request.Title,
            // Stamped at queue time, not at completion: the caregiver is told how long they have
            // when they ask, and a slow generation must not quietly shorten that window.
            ExpiresAt = now.Add(_options.Retention)
        };

        await _unitOfWork.Reports.AddAsync(report);
        await _unitOfWork.SaveChangesAsync();

        var reportId = FormatId(report.Id);
        _ = Task.Run(() => GenerateInBackground(report.Id, request));

        return new ReportQueuedResponse
        {
            ReportId = reportId,
            Status = ReportStatus.Pending,
            EstimatedReadyInSeconds = 30,
            StatusUrl = $"/api/v1/reports/{reportId}"
        };
    }

    public async Task<ReportStatusResponse?> GetStatusAsync(Guid requestingUserId, string reportId)
    {
        // A report id is a bearer-style handle, so ownership is what actually protects the
        // content. Someone else's report reads as "no such report" — same as an expired one —
        // rather than a 403 that would confirm the id is live.
        if (requestingUserId == Guid.Empty || !TryParseId(reportId, out var id))
            return null;

        var report = await _unitOfWork.Reports.GetForOwnerAsync(id, requestingUserId);

        // Past its window the row may still be here — the cleanup worker sweeps on a schedule, it
        // is not a clock. Expiry is decided by the timestamp, so a report never outlives the
        // window it advertised just because the sweep has not come round.
        if (report is null || report.ExpiresAt <= DateTime.UtcNow)
            return null;

        return ToStatusResponse(report);
    }

    /// <summary>
    /// The bytes of a ready report the requesting user owns.
    /// </summary>
    /// <remarks>
    /// The exception messages are fixed caregiver-facing copy, matching what
    /// <see cref="GetStatusAsync"/>'s caller says. <c>ReportsController</c> returns them straight
    /// to the client, so interpolating the requested id would put caller-supplied text back in a
    /// response — and, just as much to the point, would put developer copy ("Report 8f14e45f… is
    /// not ready (status: Pending)") in front of a caregiver, leaking an internal enum name with
    /// it. The id is in the request path and the trace; it does not need to be in the answer.
    /// </remarks>
    public async Task<(byte[] Content, string ContentType, string FileName)> DownloadAsync(
        Guid requestingUserId, string reportId)
    {
        if (requestingUserId == Guid.Empty || !TryParseId(reportId, out var id))
            throw new KeyNotFoundException(NotFoundMessage);

        var report = await _unitOfWork.Reports.GetForOwnerAsync(id, requestingUserId);
        if (report is null || report.ExpiresAt <= DateTime.UtcNow)
            throw new KeyNotFoundException(NotFoundMessage);

        if (report.Status != ReportStatus.Ready)
            throw new InvalidOperationException(NotReadyMessage);

        // A Ready row always names an object; a Ready row without one would be a bug in the
        // completion path, and reads as gone rather than as a null-reference 500.
        var content = report.ObjectName is null
            ? null
            : await _storage.DownloadAsync(report.ObjectName);

        // An object that has gone from the bucket ahead of its row is, from the caregiver's side,
        // the same fact as an expired report — so it is told the same way.
        if (content is null)
            throw new KeyNotFoundException(NotFoundMessage);

        return (content,
            report.ContentType ?? "application/octet-stream",
            report.FileName ?? $"report-{FormatId(report.Id)}");
    }

    /// <summary>
    /// One message for unknown, expired, another user's, and content already reaped — the four are
    /// deliberately indistinguishable, so telling them apart in the copy would undo that.
    /// </summary>
    private const string NotFoundMessage =
        "We couldn't find that report — it may have expired. Try generating a new one.";

    private const string NotReadyMessage =
        "That report isn't ready yet — give it a moment and try again.";

    /// <summary>
    /// Renders the export outside the request. Opens its own scope: the caller's returned long
    /// before this runs, taking its <see cref="IUnitOfWork"/> with it.
    /// </summary>
    private async Task GenerateInBackground(Guid reportId, GenerateReportRequest request)
    {
        using var scope = _scopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var generativeAi = scope.ServiceProvider.GetRequiredService<IGenerativeAiService>();
        var renderers = scope.ServiceProvider.GetServices<IReportRenderer>();

        var report = await unitOfWork.Reports.GetByIdAsync(reportId);
        if (report is null)
        {
            // Erased or reaped between queueing and here — nothing to render, and nothing wrong.
            _logger.LogInformation("Report {ReportId} vanished before generation started.", reportId);
            return;
        }

        try
        {
            var renderer = renderers.FirstOrDefault(r => r.Format == report.Format)
                ?? throw new NotSupportedException(
                    $"No renderer is registered for report format {report.Format}.");

            var data = await GatherAsync(unitOfWork, request);
            var sections = new ReportSections(
                request.IncludeMetrics, request.IncludeAlerts, request.IncludeDevices);

            var narrative = await BuildNarrativeAsync(generativeAi, data, report.Format);
            var rendered = await renderer.RenderAsync(data, sections, narrative);

            var objectName = await _storage.UploadAsync(
                report.OwnerUserId,
                FormatId(report.Id),
                rendered.Extension,
                rendered.ContentType,
                rendered.Content);

            report.Status = ReportStatus.Ready;
            report.ObjectName = objectName;
            report.ContentType = rendered.ContentType;
            report.FileName = BuildFileName(data, rendered.Extension);
            report.FileSizeBytes = rendered.Content.Length;
            report.CompletedAt = DateTime.UtcNow;

            unitOfWork.Reports.Update(report);
            await unitOfWork.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Report generation failed for {ReportId}", reportId);

            try
            {
                report.Status = ReportStatus.Failed;
                report.CompletedAt = DateTime.UtcNow;
                report.Error = "Report generation failed. Please try again.";

                unitOfWork.Reports.Update(report);
                await unitOfWork.SaveChangesAsync();
            }
            catch (Exception saveEx)
            {
                // The database is the thing that failed. Leave the row Pending and let the
                // cleanup worker's stale sweep fail it out, rather than losing the report id here.
                _logger.LogError(saveEx, "Could not record the failure of report {ReportId}", reportId);
            }
        }
    }

    /// <summary>
    /// Reads everything the export covers, once, in request order.
    /// </summary>
    private static async Task<ReportDataSet> GatherAsync(
        IUnitOfWork unitOfWork, GenerateReportRequest request)
    {
        var members = new List<ReportMemberData>(request.CardiMemberIds.Count);

        foreach (var memberId in request.CardiMemberIds)
        {
            var member = await unitOfWork.CardiMembers.GetByIdAsync(memberId);
            if (member is null) continue;

            // Gated like the other two sections, and for a sharper reason than symmetry: the
            // narrative prompt is built from whatever this gather returns, so loading the logs
            // regardless meant a caregiver who unticked metrics still had the readings described
            // in their PDF — and still had them sent to the general provider.
            var logs = request.IncludeMetrics
                ? (await unitOfWork.ActivityLogs
                        .GetByCardiMemberAndDateRangeAsync(memberId, request.DateRangeFrom, request.DateRangeTo))
                    .OrderBy(l => l.Date)
                    .ToList()
                : [];

            var alerts = request.IncludeAlerts
                ? (await unitOfWork.Alerts.GetByCardiMemberAsync(memberId, activeOnly: false))
                    .Where(a =>
                        DateOnly.FromDateTime(a.TriggeredDate) >= request.DateRangeFrom &&
                        DateOnly.FromDateTime(a.TriggeredDate) <= request.DateRangeTo)
                    .OrderBy(a => a.TriggeredDate)
                    .ToList()
                : [];

            var devices = request.IncludeDevices
                ? (await unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(memberId)).ToList()
                : [];

            members.Add(new ReportMemberData(member, logs, alerts, devices));
        }

        return new ReportDataSet(members, request.DateRangeFrom, request.DateRangeTo, request.Title);
    }

    /// <summary>
    /// The caregiver-facing prose, or null for formats that have no place for it.
    /// </summary>
    /// <remarks>
    /// CSV and FHIR are machine-facing: a spreadsheet column or a FHIR resource holding a
    /// paragraph of generated English would be neither useful nor safe to feed onward into an EHR.
    /// Only the PDF carries the narrative, so only the PDF pays the model call — and a caregiver
    /// asking for CSV gets their file without waiting on an inference.
    /// </remarks>
    private async Task<string?> BuildNarrativeAsync(
        IGenerativeAiService generativeAi, ReportDataSet data, ReportFormat format)
    {
        if (format != ReportFormat.Pdf)
            return null;

        var prompt = BuildReportPrompt(data);
        var generated = await generativeAi.GenerateAsync(prompt.Text);

        // Names are restored only here, after the model has answered — the provider never saw them.
        return RestoreNames(generated, prompt.Pseudonyms);
    }

    /// <summary>
    /// <c>carditrack-export-{member}-{from}-{to}.{ext}</c> for one member; the member part becomes
    /// the member count past that, because a filename is not the place to list a family.
    /// </summary>
    private static string BuildFileName(ReportDataSet data, string extension)
    {
        var subject = data.Members.Count == 1
            ? Slug(data.Members[0].Member.Name)
            : $"{data.Members.Count}-members";

        return $"carditrack-export-{subject}-{data.From:yyyyMMdd}-{data.To:yyyyMMdd}.{extension}";
    }

    /// <summary>
    /// A filename-safe rendering of a member's name. ASCII letters and digits only, so the value
    /// cannot carry a path separator, a quote, or anything else that would need escaping in the
    /// <c>Content-Disposition</c> header it ends up in.
    /// </summary>
    private static string Slug(string name)
    {
        var slug = new string(name
            .Select(c => char.IsAsciiLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-')
            .ToArray())
            .Trim('-');

        while (slug.Contains("--", StringComparison.Ordinal))
            slug = slug.Replace("--", "-", StringComparison.Ordinal);

        // A name of nothing but non-ASCII characters is a real case, not a defensive one.
        return slug.Length == 0 ? "member" : slug[..Math.Min(slug.Length, 40)];
    }

    private ReportStatusResponse ToStatusResponse(Report report) => new()
    {
        ReportId = FormatId(report.Id),
        Status = report.Status,
        Format = report.Format,
        ContentType = report.ContentType,
        FileSizeBytes = report.FileSizeBytes,
        DownloadUrl = report.Status == ReportStatus.Ready
            ? $"/api/v1/reports/{FormatId(report.Id)}/download"
            : null,
        DownloadExpiresAt = new DateTimeOffset(DateTime.SpecifyKind(report.ExpiresAt, DateTimeKind.Utc)),
        CreatedAt = new DateTimeOffset(DateTime.SpecifyKind(report.CreatedDate, DateTimeKind.Utc)),
        CompletedAt = report.CompletedAt is { } completed
            ? new DateTimeOffset(DateTime.SpecifyKind(completed, DateTimeKind.Utc))
            : null,
        Error = report.Error,
        Metadata = new ReportMetadata
        {
            CardiMembers = report.CardiMemberIds.Select(id => id.ToString()).ToList(),
            DateRangeFrom = report.DateRangeFrom,
            DateRangeTo = report.DateRangeTo
        }
    };

    /// <summary>The published id shape: a GUID in compact "N" form, 32 hex chars, no dashes.</summary>
    private static string FormatId(Guid id) => id.ToString("N");

    /// <summary>
    /// Parses a client-supplied report id. Deliberately tolerant of the dashed form too — a
    /// caregiver's client that round-trips the id through a GUID type still resolves its own
    /// report, and the ownership check is what protects the content either way.
    /// </summary>
    private static bool TryParseId(string reportId, out Guid id) => Guid.TryParse(reportId, out id);

    /// <summary>
    /// A prompt with no patient names in it, plus the mapping needed to put them back.
    /// </summary>
    private sealed record ReportPrompt(string Text, IReadOnlyDictionary<string, string> Pseudonyms);

    /// <summary>
    /// Reports go to the general provider — today Gemini's consumer endpoint, which is outside
    /// the Google Cloud BAA. Health readings alone are not identifying; a name attached to them
    /// is. Members are labelled positionally here and the labels are swapped back for real names
    /// after the response returns, so identity and health data never leave together.
    /// </summary>
    private static ReportPrompt BuildReportPrompt(ReportDataSet data)
    {
        var sections = new List<string>();
        var pseudonyms = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var member in data.Members)
        {
            var label = PseudonymFor(pseudonyms.Count);
            pseudonyms[label] = member.Member.Name;

            var sb = new StringBuilder();
            sb.AppendLine($"## {label}");

            if (member.ActivityLogs.Count > 0)
            {
                sb.AppendLine("### Activity Metrics");
                foreach (var log in member.ActivityLogs)
                    sb.AppendLine($"  {log.Date}: {DayFigures(log)}");
            }

            if (member.Alerts.Count > 0)
            {
                sb.AppendLine("### Alerts");
                foreach (var alert in member.Alerts)
                    sb.AppendLine(
                        $"  {alert.TriggeredDate:yyyy-MM-dd} [{alert.Severity}] "
                        + MedicalPromptBlocks.Flatten(alert.Title));
            }

            sections.Add(sb.ToString());
        }

        var text = MedicalPromptBlocks.Tone + $"""
            Write a health report for a non-clinical caregiver, covering {data.From} to {data.To}.

            """ + MedicalPromptBlocks.CaregiverRegister + $"""

            {string.Join("\n\n", sections)}

            Summarise the data above in a clear, structured report: say what the readings show and
            where they moved, and refer to each person by the exact label given above. Do not quote
            a figure that is not above, and where a reading was not measured, say so rather than
            leaving it to read as an ordinary one.
            """;

        return new ReportPrompt(text, pseudonyms);
    }

    /// <summary>
    /// One day's figures, naming only what the device reported.
    /// </summary>
    /// <remarks>
    /// The three readings were interpolated straight into the line, so a day the watch missed a
    /// metric rendered "steps=, HR=71, sleep=min" — an empty value beside a real one, in a
    /// document a caregiver keeps. The same shape the family digest guards against and asserts on;
    /// this is the third renderer in the review that had it.
    /// </remarks>
    private static string DayFigures(ActivityLog log)
    {
        var parts = new List<string>(3);
        if (log.Steps is { } steps)
            parts.Add($"steps={steps}");
        if (log.RestingHeartRate is { } resting)
            parts.Add($"HR={resting}");
        if (log.SleepMinutes is { } sleep)
            parts.Add($"sleep(night ending that morning)={sleep}min");

        return parts.Count > 0 ? string.Join(", ", parts) : "nothing measured";
    }

    /// <summary>
    /// Positional labels, stable within one report: "Patient A", "Patient B", … Past 26 members
    /// they become "Patient AA" and so on, so every member gets a distinct label no matter how
    /// many are requested.
    /// </summary>
    private static string PseudonymFor(int index)
    {
        var suffix = string.Empty;
        for (var n = index; ; n = n / 26 - 1)
        {
            suffix = (char)('A' + n % 26) + suffix;
            if (n < 26) break;
        }
        return $"Patient {suffix}";
    }

    /// <summary>
    /// Swaps each pseudonym in the model's answer back to the real name. Longest label first so
    /// "Patient A" cannot claim the prefix of "Patient AA".
    /// </summary>
    private static string RestoreNames(string content, IReadOnlyDictionary<string, string> pseudonyms)
    {
        foreach (var (label, name) in pseudonyms.OrderByDescending(p => p.Key.Length))
            content = content.Replace(label, name, StringComparison.Ordinal);

        return content;
    }
}
