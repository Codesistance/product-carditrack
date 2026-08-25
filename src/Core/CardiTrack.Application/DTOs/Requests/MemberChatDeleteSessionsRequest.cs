using System.ComponentModel.DataAnnotations;

namespace CardiTrack.Application.DTOs.Requests;

/// <summary>
/// The conversations a caregiver asked to permanently delete from their chat history — one id or
/// many; the history list offers multi-select. Deletion is confirmed client-side as permanent
/// before this request is ever sent.
/// </summary>
public class MemberChatDeleteSessionsRequest
{
    /// <summary>The sessions to delete. Ids that do not exist, or belong to another caregiver or
    /// member, are skipped rather than failing the batch — deleting is idempotent, and a stale id
    /// from a list refreshed elsewhere should not strand the rest of the selection.</summary>
    [Required]
    [MinLength(1)]
    [MaxLength(100)]
    public List<Guid> SessionIds { get; init; } = [];
}
