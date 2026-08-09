using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Domain.Entities;

namespace CardiTrack.Application.Services;

/// <summary>
/// How far along baseline learning is for one CardiMember. Shared by the dashboard (M1-09e)
/// and the member detail screen (M1-13) so the two can never disagree about how many days
/// have been captured.
/// </summary>
public static class BaselineProgress
{
    /// <summary>Days of history the established baseline is built from.</summary>
    public const int PeriodDays = 30;

    /// <summary>
    /// A baseline over a window shorter than <see cref="PeriodDays"/> is <em>provisional</em>:
    /// an early, low-confidence picture that lets the dashboard colour metrics and insights speak
    /// tentatively while the full window is still filling. Clients present its comparisons as
    /// early impressions, and nothing may alert off it.
    /// </summary>
    public static bool IsProvisional(PatternBaseline baseline) => baseline.PeriodDays < PeriodDays;

    /// <summary>
    /// Progress is measured in <em>distinct days that produced data</em>, not elapsed days:
    /// a member whose device sat in a drawer for a fortnight has not learned anything.
    /// Progress counts toward the established window even while a provisional baseline serves.
    /// </summary>
    public static DashboardBaselineState From(IEnumerable<ActivityLog> logs, PatternBaseline? baseline)
    {
        var daysCaptured = Math.Min(logs.Select(l => l.Date).Distinct().Count(), PeriodDays);

        return new DashboardBaselineState
        {
            IsLearning = baseline is null,
            IsProvisional = baseline is not null && IsProvisional(baseline),
            BaselinePeriodDays = baseline?.PeriodDays,
            DaysCaptured = daysCaptured,
            DaysRequired = PeriodDays,
            PercentComplete = Math.Min(100, daysCaptured * 100 / PeriodDays),
        };
    }
}
