namespace CardiTrack.Application.DTOs.Common;

/// <summary>
/// What an alert-list caller is asking for, on the wire and in <see cref="AlertQuery"/>.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not <see cref="CardiTrack.Domain.Enums.AlertStatus"/>, which is where a single
/// alert <em>sits</em> — one position per row, derived from <c>AcknowledgedDate</c> and
/// <c>IsResolved</c> by <see cref="Services.AlertLifecycle"/> and promised to be mapped the same
/// way by every projection. A filter is a different thing: it may name a set, and
/// <see cref="Open"/> names two positions at once. Adding it to the position enum would have made
/// that enum's promise untrue — <c>StatusOf</c> could never return it — so the two are separate
/// types that happen to share three names.
/// </para>
/// <para>
/// <see cref="Open"/> is what the mobile Alerts list asks for outside its archive: the screen
/// splits "current alerts" from "View Archived Alerts", and the archive is
/// <see cref="Resolved"/>. Without it the current list could only ask for everything, so resolved
/// alerts appeared in both halves — and, sharing one page of
/// <see cref="AlertQuery.DefaultLimit"/> rows with the open ones, could push an open alert off
/// the list entirely while it was still colouring the dashboard hero.
/// </para>
/// </remarks>
public enum AlertStatusFilter
{
    /// <summary>Raised and not yet acknowledged — the bell's unread set.</summary>
    New = 1,

    /// <summary>Acknowledged by a caregiver, but the episode is not over.</summary>
    Acknowledged = 2,

    /// <summary>The episode is over: the producer closed it. The archive.</summary>
    Resolved = 3,

    /// <summary>
    /// Every episode nobody has closed — <see cref="New"/> and <see cref="Acknowledged"/>
    /// together. The same set <c>MemberInsightsCalculator.ComputeHealthStatus</c> colours the
    /// dashboard hero from, so an alert the hero is speaking about is always on this list.
    /// </summary>
    Open = 4,
}
