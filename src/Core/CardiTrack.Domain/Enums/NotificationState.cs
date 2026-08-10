using System.ComponentModel.DataAnnotations;

namespace CardiTrack.Domain.Enums;

/// <summary>
/// Where a notification sits in its lifecycle.
/// </summary>
/// <remarks>
/// There is deliberately no <c>Dismissed</c> state. Dismissing writes a
/// <see cref="Entities.NotificationMute"/> and resolves the row, so "don't show this" is recorded
/// in exactly one place — the same reasoning that keeps <see cref="AlertStatus"/> derived rather
/// than stored.
/// </remarks>
public enum NotificationState
{
    /// <summary>Visible and actionable.</summary>
    [Display(Name = "Open")]
    Open = 1,

    /// <summary>Hidden until <c>SnoozedUntil</c> passes, then reopened by the next run.</summary>
    [Display(Name = "Snoozed")]
    Snoozed = 2,

    /// <summary>The gap closed, or the user dismissed it. Terminal.</summary>
    [Display(Name = "Resolved")]
    Resolved = 3,

    /// <summary>
    /// Withdrawn by circumstance rather than settled — monitoring paused, member removed.
    /// Terminal, and distinguished from <see cref="Resolved"/> so comply-rate metrics do not
    /// count a pause as a success.
    /// </summary>
    [Display(Name = "Superseded")]
    Superseded = 4
}
