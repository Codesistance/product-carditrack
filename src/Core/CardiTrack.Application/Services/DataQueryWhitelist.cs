using CardiTrack.Application.DTOs.Common;
using CardiTrack.Application.Interfaces.Repositories;

namespace CardiTrack.Application.Services;

/// <summary>
/// Resolves a <see cref="DataQueryPlan"/> against one CardiMember's already-audited repository
/// reads. The parameterized-query analog: <paramref name="cardiMemberId"/> in
/// <see cref="ExecuteAsync"/> is the bind variable a caller supplies from trusted state, never a
/// field the plan carries — <see cref="DataQueryPlan"/>'s type makes that the only option, and this
/// class does not add a way around it (it never reads a member id from the plan, because there is
/// none to read).
/// </summary>
public static class DataQueryWhitelist
{
    /// <summary>Floor/ceiling regardless of what the model asked for — the model's number is a
    /// preference, not a grant.</summary>
    private const int MinRecentActivityDays = 1;
    private const int MaxRecentActivityDays = 14;
    private const int MinRealtimeAssessmentHours = 1;
    private const int MaxRealtimeAssessmentHours = 72;

    public static async Task<FetchedMemberData> ExecuteAsync(
        DataQueryPlan plan, Guid cardiMemberId, IUnitOfWork unitOfWork, DateTime utcNow, CancellationToken ct)
    {
        var sources = new HashSet<DataQueryKind>(plan.Sources);

        var recentActivity = sources.Contains(DataQueryKind.RecentActivity)
            ? (await unitOfWork.ActivityLogs.GetByCardiMemberAndDateRangeAsync(
                cardiMemberId,
                DateOnly.FromDateTime(utcNow).AddDays(-Clamp(plan.RecentActivityDays, MinRecentActivityDays, MaxRecentActivityDays)),
                DateOnly.FromDateTime(utcNow))).ToList()
            : [];

        var baseline = sources.Contains(DataQueryKind.Baseline)
            ? await unitOfWork.PatternBaselines.GetLatestByCardiMemberAsync(cardiMemberId, periodDays: 30)
            : null;

        var unresolvedAlerts = sources.Contains(DataQueryKind.UnresolvedAlerts)
            ? await unitOfWork.Alerts.GetUnresolvedByCardiMemberAsync(cardiMemberId)
            : [];

        var realtimeAssessments = sources.Contains(DataQueryKind.RealtimeAssessments)
            ? await unitOfWork.RealtimeAssessments.GetSinceAsync(
                cardiMemberId,
                utcNow.AddHours(-Clamp(plan.RealtimeAssessmentHours, MinRealtimeAssessmentHours, MaxRealtimeAssessmentHours)),
                ct)
            : [];

        return new FetchedMemberData
        {
            RecentActivity = recentActivity,
            Baseline = baseline,
            UnresolvedAlerts = unresolvedAlerts,
            RealtimeAssessments = realtimeAssessments,
        };
    }

    private static int Clamp(int value, int min, int max) => Math.Max(min, Math.Min(max, value));
}
