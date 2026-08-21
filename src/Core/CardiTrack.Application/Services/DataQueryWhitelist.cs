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
    /// <remarks>
    /// The activity ceiling came down from 14 days to one week on 2026-08-21, for latency rather
    /// than privacy. Every fetched day is rows in the clinical prompt, and that prompt is
    /// evaluated on a CPU-served model at roughly 25 tokens/sec — a measured chat send spent 47.6 s
    /// on prompt evaluation alone before its first output token. A fortnight of readings is twice
    /// that bill for a question a caregiver almost never asks: the planner's own default has always
    /// been 7, so the ceiling was only ever reachable by the model asking for more, and the answer
    /// it bought did not justify the wait it cost.
    /// </remarks>
    private const int MinRecentActivityDays = 1;
    private const int MaxRecentActivityDays = 7;
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
