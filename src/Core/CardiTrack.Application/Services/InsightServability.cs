using System.Diagnostics.CodeAnalysis;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Services;

/// <summary>
/// How stale a persisted <see cref="MemberInsight"/> may be before it is treated as though
/// generation has stopped for that member, and whether a given row may be shown at all.
/// </summary>
/// <remarks>
/// A predicate over the row rather than a method on it, following <see cref="AdviseServability"/>:
/// the entity is a persistence shape, and when a row may be served is a decision its readers
/// share rather than a property of the record. Keeping it in one place is what stops the alert
/// screen and the dashboard drifting into disagreeing about whether an insight exists — the
/// failure <see cref="AdviseStaleness"/> was extracted after.
/// </remarks>
public static class InsightServability
{
    /// <summary>
    /// Wider than the status line's ceiling and matching <see cref="AdviseStaleness.MaxAge"/>:
    /// the baseline and rolling trend reads describe a picture measured in weeks, so a row a day
    /// or two old is still describing the same picture. This is a buffer against missed passes,
    /// not the cadence itself — which is what <see cref="MaxAgeFor"/> keeps true at the horizons
    /// whose cadence is not daily.
    /// </summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(3);

    /// <summary>
    /// The ceiling for <see cref="InsightScope.TrendWeekly"/>: its own seven-day cadence plus the
    /// same three days of slack the daily rows get.
    /// </summary>
    public static readonly TimeSpan WeeklyMaxAge = TimeSpan.FromDays(10);

    /// <summary>
    /// The ceiling for <see cref="InsightScope.TrendMonthly"/>: longer than the longest gap
    /// between two monthly passes — thirty-one days, January to February — plus slack, and still
    /// well inside <see cref="InsightRetention.MaxAge"/>, so a row is never swept while it is
    /// still the current one.
    /// </summary>
    public static readonly TimeSpan MonthlyMaxAge = TimeSpan.FromDays(40);

    /// <summary>
    /// How stale a row of this scope may be before it is treated as though generation has stopped.
    /// </summary>
    /// <remarks>
    /// The ceiling is a buffer on top of the cadence that writes the row, so it has to move with
    /// that cadence. At the flat three days a weekly narrative would be withheld on four days in
    /// seven and a monthly one on twenty-seven in thirty — which reads to a caregiver as the
    /// feature not existing rather than as a row that aged out. Each horizon carries its own
    /// cadence plus slack rather than every scope taking the widest, so what the ceiling is
    /// actually for still holds at each of them.
    /// </remarks>
    public static TimeSpan MaxAgeFor(InsightScope scope) => scope switch
    {
        InsightScope.TrendWeekly => WeeklyMaxAge,
        InsightScope.TrendMonthly => MonthlyMaxAge,
        _ => MaxAge,
    };

    /// <summary>
    /// True when <paramref name="insight"/> exists, says something, and is inside the ceiling
    /// <see cref="MaxAgeFor"/> gives its scope.
    /// </summary>
    /// <remarks>
    /// An alert-scoped row is exempt from the age check, and deliberately: it explains one event
    /// that happened at a fixed moment and does not go out of date the way a reading of the
    /// current picture does. A caregiver opening a three-week-old alert from their history should
    /// still be told why it fired — withholding the explanation because the explanation is old
    /// would withhold it exactly when they have least other context.
    /// </remarks>
    public static bool IsServable(
        [NotNullWhen(true)] MemberInsight? insight, DateTime utcNow)
    {
        if (insight is null || string.IsNullOrWhiteSpace(insight.Summary))
            return false;

        return insight.AlertId is not null
            || utcNow - insight.GeneratedAtUtc <= MaxAgeFor(insight.Scope);
    }
}
