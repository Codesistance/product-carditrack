using System.Diagnostics.CodeAnalysis;
using CardiTrack.Domain.Entities;

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
    /// the baseline and trend reads describe a picture measured in weeks, so a row a day or two
    /// old is still describing the same picture. This is a buffer against missed passes, not the
    /// cadence itself.
    /// </summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(3);

    /// <summary>
    /// True when <paramref name="insight"/> exists, says something, and is inside
    /// <see cref="MaxAge"/>.
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
            || utcNow - insight.GeneratedAtUtc <= MaxAge;
    }
}
