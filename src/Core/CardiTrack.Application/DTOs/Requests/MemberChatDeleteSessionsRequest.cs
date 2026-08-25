using System.ComponentModel.DataAnnotations;

namespace CardiTrack.Application.DTOs.Requests;

/// <summary>
/// The conversations a caregiver asked to permanently delete from their chat history — one id or
/// many; the history list offers multi-select. Deletion is confirmed client-side as permanent
/// before this request is ever sent.
/// </summary>
public class MemberChatDeleteSessionsRequest
{
    /// <summary>The most sessions one call may delete — the model-validation cap here, the
    /// service's own guard, and the app's batching all share this number.</summary>
    public const int MaxBatchSize = 100;

    /// <summary>The sessions to delete. Ids that do not exist, or belong to another caregiver or
    /// member, are skipped rather than failing the batch — deleting is idempotent, and a stale id
    /// from a list refreshed elsewhere should not strand the rest of the selection. An empty list
    /// is different: over HTTP it is a malformed request and model validation rejects it before
    /// the action runs (the app never sends one — its delete pill disables at zero selected),
    /// while the service keeps its own empty no-op as defence for non-HTTP callers.</summary>
    [Required]
    [MinLength(1)]
    [MaxLength(MaxBatchSize)]
    public List<Guid> SessionIds { get; init; } = [];
}
