using CardiTrack.Domain.Common;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Domain.Entities;

/// <summary>
/// A caregiver's request to re-read a stretch of one connection's history from its provider —
/// the M1-15 "Re-pull History" action. The row is both the work order the Worker executes and
/// the record of who asked for what, when, and how far it got.
/// </summary>
/// <remarks>
/// <para>
/// Exists because the routine sync only ever re-reads a short trailing window, and the
/// autonomous backfill walks a connection's history exactly once. A day the provider revised
/// late, or a stretch the Worker missed while it was down, has no path back into the store
/// without something that can be pointed at a range and told to fetch it again. This is that
/// something, bounded so a tap cannot spend a wearer's whole provider quota.
/// </para>
/// <para>
/// Progress walks newest-first, like the backfill: <see cref="CompletedTo"/> is the oldest day
/// fetched so far, and the request is done when it reaches <see cref="FromDate"/>. Re-pulled
/// days are upserted — a re-pull never deletes a stored day, and a day the provider has nothing
/// for is left as it was. Not soft-deletable: a finished request is history, not a thing a
/// caregiver removes. No health data lives here — dates, counts and status only.
/// </para>
/// </remarks>
public class DeviceHistoryRepull : BaseEntity
{
    public Guid DeviceConnectionId { get; set; }

    public Guid CardiMemberId { get; set; }

    /// <summary>The caregiver who asked. Audit only; nothing is gated on it afterwards.</summary>
    public Guid RequestedByUserId { get; set; }

    /// <summary>Oldest day in the range, inclusive.</summary>
    public DateOnly FromDate { get; set; }

    /// <summary>Newest day in the range, inclusive — the day before the request, since today is the routine sync's.</summary>
    public DateOnly ToDate { get; set; }

    /// <summary>
    /// Oldest day fetched so far, walking back from <see cref="ToDate"/>; null until the first
    /// chunk lands. Equal to <see cref="FromDate"/> once the request is complete.
    /// </summary>
    public DateOnly? CompletedTo { get; set; }

    /// <summary>Days in the fetched stretch the provider actually had data for.</summary>
    public int DaysWithData { get; set; }

    /// <summary>
    /// Chunk attempts that ended in a provider failure. The Worker retries a failed chunk on its
    /// next pass and gives up on the request once this reaches its ceiling.
    /// </summary>
    public int Attempts { get; set; }

    public HistoryRepullStatus Status { get; set; } = HistoryRepullStatus.Pending;

    public DateTime RequestedAt { get; set; }

    public DateTime? StartedAt { get; set; }

    /// <summary>When the request reached a terminal status — completed, failed or cancelled.</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// A short, payload-free label for why a request failed or was cancelled — an exception type
    /// name or a refusal code, never a provider response body.
    /// </summary>
    public string? FailureReason { get; set; }

    /// <summary>Whether the Worker still has something to do for this request.</summary>
    public bool IsOpen => Status is HistoryRepullStatus.Pending or HistoryRepullStatus.InProgress;
}
