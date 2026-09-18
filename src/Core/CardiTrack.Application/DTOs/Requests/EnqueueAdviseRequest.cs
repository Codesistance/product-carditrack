namespace CardiTrack.Application.DTOs.Requests;

/// <summary>
/// POST /api/v1/internal/notifications/enqueue-advise — service-to-service only, called by the
/// digest job once it has persisted new <see cref="Domain.Entities.MemberAdvise"/> rows.
/// Deliberately carries only <see cref="CardiMemberId"/>: which users get notified is always
/// resolved server-side from <c>UserCardiMember.ReceiveAlerts</c>, never trusted from the caller.
/// </summary>
public class EnqueueAdviseRequest
{
    public Guid CardiMemberId { get; set; }
}
