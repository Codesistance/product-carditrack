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
    private readonly ILogger<ReportGenerationService> _logger;

    private static readonly TimeSpan ReportTtl = TimeSpan.FromHours(1);

    public ReportGenerationService(
        IGenerativeAiService generativeAi,
        IUnitOfWork unitOfWork,
        IDistributedCache cache,
        ILogger<ReportGenerationService> logger)
    {
        _generativeAi = generativeAi;
        _unitOfWork = unitOfWork;
        _cache = cache;
        _logger = logger;
    }

    public async Task<ReportQueuedResponse> GenerateAsync(Guid requestingUserId, GenerateReportRequest request)
    {
        var reportId = Guid.NewGuid().ToString("N");
        var statusKey = ReportKey(reportId);

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

        await _cache.SetStringAsync(statusKey, Serialize(initialStatus),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ReportTtl });

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
        var json = await _cache.GetStringAsync(ReportKey(reportId));
        return json is null ? null : Deserialize<ReportStatusResponse>(json);
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

            var status = await GetStatusAsync(requestingUserId, reportId);
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

            await _cache.SetStringAsync(ReportKey(reportId), Serialize(updated),
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ReportTtl });
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
            await _cache.SetStringAsync(ReportKey(reportId), Serialize(failed),
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ReportTtl });
        }
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
    private static T Deserialize<T>(string json) => JsonUtility.Deserialize<T>(json);
}
