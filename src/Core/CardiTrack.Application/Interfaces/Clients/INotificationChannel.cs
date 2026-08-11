using CardiTrack.Domain.Entities;

namespace CardiTrack.Application.Interfaces.Clients;

/// <summary>
/// The send mechanism for one delivery. FCM HTTP v1 today (relaying to APNs for iOS), behind this
/// seam so a future direct-APNs path wouldn't touch any caller (notification_engine.md §2, §6.2).
/// </summary>
public interface INotificationChannel
{
    Task<SendResult> SendAsync(NotificationDelivery delivery, PushDeviceToken token, CancellationToken ct = default);
}

/// <summary>
/// The three outcomes a channel can report (§6.2) — a closed set, not an exception, because
/// "the provider rejected this" is routine control flow for a dispatch loop, not a fault.
/// </summary>
public abstract record SendResult
{
    private SendResult() { }

    public sealed record Sent(string ProviderMessageId) : SendResult;

    /// <summary>Transient — the Worker retries with backoff up to the message TTL.</summary>
    public sealed record Retryable(string Reason) : SendResult;

    /// <summary>
    /// FCM <c>UNREGISTERED</c> / APNs <c>410</c> — the token is dead. Disables it immediately,
    /// which arms <c>PUSH_UNREACHABLE</c>: the failure feeds the engine back.
    /// </summary>
    public sealed record Permanent(string Reason) : SendResult;
}
