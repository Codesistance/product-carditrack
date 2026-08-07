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
    /// <summary>Days of history a baseline is built from.</summary>
    public const int PeriodDays = 30;

    /// <summary>
    /// Progress is measured in <em>distinct days that produced data</em>, not elapsed days:
    /// a member whose device sat in a drawer for a fortnight has not learned anything.
    /// </summary>
    public static DashboardBaselineState From(IEnumerable<ActivityLog> logs, PatternBaseline? baseline)
    {
        var daysCaptured = Math.Min(logs.Select(l => l.Date).Distinct().Count(), PeriodDays);

        return new DashboardBaselineState
        {
            IsLearning = baseline is null,
            DaysCaptured = daysCaptured,
            DaysRequired = PeriodDays,
            PercentComplete = Math.Min(100, daysCaptured * 100 / PeriodDays),
        };
    }
}
