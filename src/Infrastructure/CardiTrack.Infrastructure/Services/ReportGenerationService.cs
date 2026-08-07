using System.Text;
using System.Text.Json;
using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Domain.Enums;
using CardiTrack.Shared.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;

namespace CardiTrack.Infrastructure.Services;

public class ReportGenerationService : IReportGenerationService
{
    private readonly IGenerativeAiService _generativeAi;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IDistributedCache _cache;
    private readonly ICardiMemberAccessService _access;
    private readonly ILogger<ReportGenerationService> _logger;

    private static readonly TimeSpan ReportTtl = TimeSpan.FromHours(1);

    public ReportGenerationService(
        IGenerativeAiService generativeAi,
        IUnitOfWork unitOfWork,
        IDistributedCache cache,
        ICardiMemberAccessService access,
        ILogger<ReportGenerationService> logger)
    {
        _generativeAi = generativeAi;
        _unitOfWork = unitOfWork;
        _cache = cache;
        _access = access;
        _logger = logger;
    }

    public async Task<ReportQueuedResponse> GenerateAsync(Guid requestingUserId, GenerateReportRequest request)
    {
        // Checked here, before anything is queued, so an unauthorised request fails as a 404 on
        // the call rather than as a silently-abandoned background job. Because the whole set is
        // vetted up front, prompt building below can trust every id in the request.
        await _access.RequireViewAccessAsync(requestingUserId, request.CardiMemberIds);

        var reportId = Guid.NewGuid().ToString("N");

        var initialStatus = new ReportStatusResponse
        {
            ReportId = reportId,
            Status = ReportStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
            Metadata = new ReportMetadata
            {
                CardiMembers = request.CardiMemberIds.Select(id => id.ToString()).ToList(),
                DateRangeFrom = request.DateRangeFrom,
                DateRangeTo = request.DateRangeTo
            }
        };

        await WriteStatusAsync(reportId, requestingUserId, initialStatus);

        _ = Task.Run(() => GenerateInBackground(reportId, requestingUserId, request));

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
        var cached = await ReadAsync(reportId);

        // A report id is a bearer-style handle, so ownership is what actually protects the
        // content. Someone else's report reads as "no such report" — same as an expired one —
        // rather than a 403 that would confirm the id is live.
        if (cached is null || requestingUserId == Guid.Empty || cached.OwnerUserId != requestingUserId)
            return null;

        return cached.Status;
    }

    public async Task<(byte[] Content, string ContentType, string FileName)> DownloadAsync(
        Guid requestingUserId, string reportId)
    {
        var status = await GetStatusAsync(requestingUserId, reportId);

        if (status is null)
            throw new KeyNotFoundException($"Report {reportId} not found or has expired.");
        if (status.Status != ReportStatus.Ready)
            throw new InvalidOperationException($"Report {reportId} is not ready (status: {status.Status}).");

        var contentJson = await _cache.GetStringAsync(ContentKey(reportId));
        if (contentJson is null)
            throw new KeyNotFoundException($"Report {reportId} content not found.");

        var bytes = Encoding.UTF8.GetBytes(contentJson);
        return (bytes, "text/plain; charset=utf-8", $"report-{reportId}.txt");
    }

    private async Task GenerateInBackground(string reportId, Guid requestingUserId, GenerateReportRequest request)
    {
        try
        {
            var prompt = await BuildReportPromptAsync(request);
            var content = await _generativeAi.GenerateAsync(prompt);

            await _cache.SetStringAsync(ContentKey(reportId), content,
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ReportTtl });

            var status = (await ReadAsync(reportId))?.Status;
            var updated = new ReportStatusResponse
            {
                ReportId = reportId,
                Status = ReportStatus.Ready,
                CompletedAt = DateTimeOffset.UtcNow,
                ContentType = "text/plain",
                FileSizeBytes = Encoding.UTF8.GetByteCount(content),
                DownloadUrl = $"/api/v1/reports/{reportId}/download",
                DownloadExpiresAt = DateTimeOffset.UtcNow.Add(ReportTtl),
                CreatedAt = status?.CreatedAt ?? DateTimeOffset.UtcNow,
                Format = status?.Format,
                Metadata = status?.Metadata
            };

            await WriteStatusAsync(reportId, requestingUserId, updated);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Report generation failed for {ReportId}", reportId);
            var failed = new ReportStatusResponse
            {
                ReportId = reportId,
                Status = ReportStatus.Failed,
                CreatedAt = DateTimeOffset.UtcNow,
                CompletedAt = DateTimeOffset.UtcNow,
                Error = "Report generation failed. Please try again."
            };
            await WriteStatusAsync(reportId, requestingUserId, failed);
        }
    }

    /// <summary>Reads the cache envelope — status plus owner. Callers outside this type go through
    /// <see cref="GetStatusAsync"/>, which applies the ownership check.</summary>
    /// <remarks>
    /// Deliberately lenient, unlike the strict <see cref="JsonUtility.Deserialize{T}"/> used for
    /// payloads we control. An entry written in an older shape, truncated, or otherwise unreadable
    /// is a cache miss — not a 500 on someone polling their report. Strictness here would also put
    /// a preview of the cached payload, CardiMember ids included, into the exception message and
    /// from there into the logs.
    /// </remarks>
    private async Task<CachedReport?> ReadAsync(string reportId)
    {
        var json = await _cache.GetStringAsync(ReportKey(reportId));
        if (json is null)
            return null;

        if (!JsonUtility.TryDeserialize<CachedReport>(json, out var cached, out var errors)
            || cached?.Status is null)
        {
            // Locations only — the error text can quote the offending value, and this cache holds
            // health-report metadata.
            _logger.LogWarning(
                "Discarding unreadable report cache entry {ReportId}; {ErrorCount} error(s) at {ErrorLocations}",
                reportId,
                errors.Count,
                string.Join(", ", errors.Select(e =>
                    $"{(e.Path.Length == 0 ? "$" : e.Path)}@{e.LineNumber}:{e.LinePosition}")));

            await EvictAsync(reportId);
            return null;
        }

        return cached;
    }

    /// <summary>Drops a report's status and body together — the body is unreachable once the
    /// status entry it is gated behind is gone.</summary>
    private async Task EvictAsync(string reportId)
    {
        await _cache.RemoveAsync(ReportKey(reportId));
        await _cache.RemoveAsync(ContentKey(reportId));
    }

    private Task WriteStatusAsync(string reportId, Guid ownerUserId, ReportStatusResponse status) =>
        _cache.SetStringAsync(
            ReportKey(reportId),
            Serialize(new CachedReport { OwnerUserId = ownerUserId, Status = status }),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ReportTtl });

    /// <summary>
    /// What actually goes in the cache: the client-facing status wrapped with the id of the user
    /// entitled to it. Internal to this service — the owner id is never returned to a caller.
    /// </summary>
    private sealed class CachedReport
    {
        public Guid OwnerUserId { get; set; }
        public ReportStatusResponse Status { get; set; } = null!;
    }

    private async Task<string> BuildReportPromptAsync(GenerateReportRequest request)
    {
        var sections = new List<string>();

        foreach (var memberId in request.CardiMemberIds)
        {
            var member = await _unitOfWork.CardiMembers.GetByIdAsync(memberId);
            if (member is null) continue;

            var logs = await _unitOfWork.ActivityLogs
                .GetByCardiMemberAndDateRangeAsync(memberId, request.DateRangeFrom, request.DateRangeTo);

            var sb = new StringBuilder();
            sb.AppendLine($"## Patient: {member.Name}");

            if (request.IncludeMetrics && logs.Any())
            {
                sb.AppendLine("### Activity Metrics");
                foreach (var log in logs.OrderBy(l => l.Date))
                    sb.AppendLine($"  {log.Date}: steps={log.Steps}, HR={log.RestingHeartRate}, sleep={log.SleepMinutes}min");
            }

            if (request.IncludeAlerts)
            {
                var alerts = await _unitOfWork.Alerts.GetByCardiMemberAsync(memberId, activeOnly: false);
                var inRange = alerts.Where(a =>
                    DateOnly.FromDateTime(a.TriggeredDate) >= request.DateRangeFrom &&
                    DateOnly.FromDateTime(a.TriggeredDate) <= request.DateRangeTo).ToList();

                if (inRange.Any())
                {
                    sb.AppendLine("### Alerts");
                    foreach (var alert in inRange)
                        sb.AppendLine($"  {alert.TriggeredDate:yyyy-MM-dd} [{alert.Severity}] {alert.Title}");
                }
            }

            sections.Add(sb.ToString());
        }

        return $"""
            You are a medical AI assistant generating a health report.
            Report format: {request.Format}
            Period: {request.DateRangeFrom} to {request.DateRangeTo}

            {string.Join("\n\n", sections)}

            Generate a clear, structured health report summarising the above data.
            Include trend observations and any patterns worth noting.
            Keep the language appropriate for a non-clinical caregiver.
            """;
    }

    private static string ReportKey(string reportId) => $"report:status:{reportId}";
    private static string ContentKey(string reportId) => $"report:content:{reportId}";
    private static string Serialize<T>(T obj) => JsonSerializer.Serialize(obj);
}
