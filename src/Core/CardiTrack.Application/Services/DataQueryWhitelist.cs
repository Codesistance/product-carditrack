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
    /// <para>
    /// The activity ceiling came down from 14 days to one week on 2026-08-21, for latency rather
    /// than privacy. Every fetched day is rows in the clinical prompt, and that prompt is
    /// evaluated on a CPU-served model at roughly 25 tokens/sec — a measured chat send spent 47.6 s
    /// on prompt evaluation alone before its first output token. A fortnight of readings is twice
    /// that bill for a question a caregiver almost never asks: the planner's own default has always
    /// been 7, so the ceiling was only ever reachable by the model asking for more, and the answer
    /// it bought did not justify the wait it cost.
    /// </para>
    /// <para>
    /// <b>This ceiling is the chat's alone.</b> <c>MemberChatService</c> is the only caller of this
    /// class, and deliberately so: chat is the one AI surface a caregiver waits on in real time, so
    /// it is the one that trades breadth for speed. Nothing else narrows — the dashboard's 30-day
    /// baselines, the 14-day trend charts, the digests and the journal all keep their own windows,
    /// because a batch job or a scrollable chart pays for a longer range in somebody's patience.
    /// </para>
    /// </remarks>
    private const int MinRecentActivityDays = 1;
    private const int MaxRecentActivityDays = 7;
    private const int MinRealtimeAssessmentHours = 1;
    private const int MaxRealtimeAssessmentHours = 72;

    public static async Task<FetchedMemberData> ExecuteAsync(
        DataQueryPlan plan, Guid cardiMemberId, IUnitOfWork unitOfWork, DateTime utcNow, CancellationToken ct)
    {
        var sources = new HashSet<DataQueryKind>(plan.Sources);

        var today = DateOnly.FromDateTime(utcNow);

        // Inclusive on both ends, so N days back from today is N-1 subtracted, not N — the repository
        // filters `>= from && <= to` and would otherwise return one day more than was asked for.
        // Matches how the rest of the codebase counts a window (StatusLineGenerationService reads
        // three days as today.AddDays(-2)..today), and means a one-week ceiling fetches a week.
        var window = sources.Contains(DataQueryKind.RecentActivity)
            ? (From: today.AddDays(-(Clamp(plan.RecentActivityDays, MinRecentActivityDays, MaxRecentActivityDays) - 1)), To: today)
            : ((DateOnly From, DateOnly To)?)null;

        var recentActivity = window is { } days
            ? (await unitOfWork.ActivityLogs.GetByCardiMemberAndDateRangeAsync(
                cardiMemberId, days.From, days.To)).ToList()
            : [];

        // Always, whether or not the plan named it. "How is he doing this afternoon?" resolved to
        // today's readings alone and came back as "his heart rate is 72 and he took 774 steps" — a
        // recitation, because with nothing to compare against there was no answer to give. The
        // baseline is what makes a number mean something, it is one row and about forty tokens,
        // and no question about a person's health is worse for knowing what is usual for them.
        // The plan keeps Baseline in its vocabulary so the planner can still say it needs it; the
        // fetch simply no longer depends on the planner having thought of it.
        var baseline = await unitOfWork.PatternBaselines
            .GetLatestByCardiMemberAsync(cardiMemberId, periodDays: 30);

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
            RecentActivityWindow = window,
            Baseline = baseline,
            UnresolvedAlerts = unresolvedAlerts,
            RealtimeAssessments = realtimeAssessments,
        };
    }

    private static int Clamp(int value, int min, int max) => Math.Max(min, Math.Min(max, value));
}
