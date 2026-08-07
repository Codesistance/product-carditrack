using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;

namespace CardiTrack.Infrastructure.Services;

public class HealthInsightService : IHealthInsightService
{
    private readonly IMedicalAiService _medicalAi;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICardiMemberAccessService _access;

    public HealthInsightService(
        IMedicalAiService medicalAi, IUnitOfWork unitOfWork, ICardiMemberAccessService access)
    {
        _medicalAi = medicalAi;
        _unitOfWork = unitOfWork;
        _access = access;
    }

    public async Task<AlertInsightResponse> AnalyzeAlertAsync(
        Guid requestingUserId, Guid alertId, CancellationToken ct = default)
    {
        // An alert is reachable only through its CardiMember. Both "no such alert" and
        // "not your alert" report the same not-found so the alert id cannot be probed.
        var alert = await _unitOfWork.Alerts.GetByIdWithCardiMemberAsync(alertId);
        if (alert is null || !await _access.HasViewAccessAsync(requestingUserId, alert.CardiMemberId, ct))
            throw new KeyNotFoundException($"Alert {alertId} not found.");

        var to = DateOnly.FromDateTime(DateTime.UtcNow);
        var from = to.AddDays(-7);
        var recentLogs = await _unitOfWork.ActivityLogs
            .GetByCardiMemberAndDateRangeAsync(alert.CardiMemberId, from, to);

        var baseline = await _unitOfWork.PatternBaselines
            .GetLatestByCardiMemberAsync(alert.CardiMemberId, 30);

        var prompt = BuildAlertPrompt(alert, recentLogs, baseline);
        var aiResponse = await _medicalAi.GenerateAsync(prompt, ct);

        return new AlertInsightResponse
        {
            AlertId = alertId,
            Explanation = aiResponse,
            Severity = alert.Severity,
            RecommendedAction = ExtractRecommendation(aiResponse)
        };
    }

    public async Task<BaselineInsightResponse> AnalyzeBaselineAsync(
        Guid requestingUserId, Guid cardiMemberId, CancellationToken ct = default)
    {
        await _access.RequireViewAccessAsync(requestingUserId, cardiMemberId, ct);

        var baselines = await Task.WhenAll(
            _unitOfWork.PatternBaselines.GetLatestByCardiMemberAsync(cardiMemberId, 30),
            _unitOfWork.PatternBaselines.GetLatestByCardiMemberAsync(cardiMemberId, 60),
            _unitOfWork.PatternBaselines.GetLatestByCardiMemberAsync(cardiMemberId, 90));

        var to = DateOnly.FromDateTime(DateTime.UtcNow);
        var from = to.AddDays(-14);
        var recentLogs = await _unitOfWork.ActivityLogs
            .GetByCardiMemberAndDateRangeAsync(cardiMemberId, from, to);

        var prompt = BuildBaselinePrompt(cardiMemberId, baselines.Where(b => b is not null)!, recentLogs);
        var aiResponse = await _medicalAi.GenerateAsync(prompt, ct);

        return new BaselineInsightResponse
        {
            CardiMemberId = cardiMemberId,
            Summary = aiResponse,
            KeyFindings = ExtractKeyFindings(aiResponse),
            GeneratedAt = DateTimeOffset.UtcNow
        };
    }

    private static string BuildAlertPrompt(
        Domain.Entities.Alert alert,
        IEnumerable<Domain.Entities.ActivityLog> recentLogs,
        Domain.Entities.PatternBaseline? baseline)
    {
        var baselineInfo = baseline is null ? "No baseline data available." :
            $"30-day baseline — Steps: {baseline.AvgSteps}±{baseline.StdDevSteps}, " +
            $"Resting HR: {baseline.AvgRestingHeartRate}±{baseline.StdDevHeartRate}, " +
            $"Sleep: {baseline.AvgSleepMinutes} min";

        var recentSummary = recentLogs.Any()
            ? string.Join(", ", recentLogs.TakeLast(3).Select(l =>
                $"{l.Date}: steps={l.Steps}, HR={l.RestingHeartRate}, sleep={l.SleepMinutes}min"))
            : "No recent activity data.";

        return $"""
            You are a medical AI assistant analyzing a health alert.

            Alert type: {alert.AlertType}
            Severity: {alert.Severity}
            Title: {alert.Title}
            Message: {alert.Message}
            Triggered: {alert.TriggeredDate:yyyy-MM-dd HH:mm} UTC
            Metric values: {alert.MetricValues ?? "none"}

            Patient baseline ({baselineInfo})
            Recent activity (last 3 days): {recentSummary}

            Provide:
            1. A clear explanation of what this alert means clinically.
            2. Likely contributing factors based on the recent data.
            3. A concise recommended action for the caregiver.

            Keep the response factual, concise, and suitable for a non-clinical caregiver.
            """;
    }

    private static string BuildBaselinePrompt(
        Guid cardiMemberId,
        IEnumerable<Domain.Entities.PatternBaseline> baselines,
        IEnumerable<Domain.Entities.ActivityLog> recentLogs)
    {
        var baselineLines = baselines.Select(b =>
            $"{b.PeriodDays}-day — Steps: {b.AvgSteps}±{b.StdDevSteps}, " +
            $"HR: {b.AvgRestingHeartRate}±{b.StdDevHeartRate}, Sleep: {b.AvgSleepMinutes} min");

        var recentSummary = recentLogs.Any()
            ? string.Join("\n", recentLogs.TakeLast(7).Select(l =>
                $"  {l.Date}: steps={l.Steps}, HR={l.RestingHeartRate}, sleep={l.SleepMinutes}min"))
            : "No recent activity data.";

        return $"""
            You are a medical AI assistant performing a health trend analysis.

            Patient baselines:
            {string.Join("\n", baselineLines)}

            Recent activity (last 7 days):
            {recentSummary}

            Provide:
            1. A brief summary of the patient's overall health trends.
            2. Key findings — list each on its own line starting with "-".
            3. Any patterns that warrant caregiver attention.

            Keep the response factual and suitable for a non-clinical caregiver.
            """;
    }

    private static string ExtractRecommendation(string aiResponse)
    {
        var lines = aiResponse.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var rec = lines.FirstOrDefault(l =>
            l.Contains("recommend", StringComparison.OrdinalIgnoreCase) ||
            l.Contains("action", StringComparison.OrdinalIgnoreCase) ||
            l.StartsWith("3.", StringComparison.Ordinal));
        return rec?.TrimStart('0', '1', '2', '3', '.', ' ') ?? "Monitor the patient and consult a healthcare provider if symptoms persist.";
    }

    private static IReadOnlyList<string> ExtractKeyFindings(string aiResponse)
    {
        return aiResponse
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.TrimStart().StartsWith('-'))
            .Select(l => l.TrimStart('-', ' '))
            .ToList();
    }
}
