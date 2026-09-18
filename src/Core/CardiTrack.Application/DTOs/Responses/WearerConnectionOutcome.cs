namespace CardiTrack.Application.DTOs.Responses;

/// <summary>How a wearer's own attempt to authorize their wearable ended.</summary>
public enum WearerConnectionResult
{
    /// <summary>The grant succeeded and a connection was stored.</summary>
    Completed = 1,

    /// <summary>
    /// The wearer declined at the provider, or the provider refused. Not a failure of ours, and the
    /// invite stays live so they can change their mind without the caregiver sending another.
    /// </summary>
    Denied = 2,

    /// <summary>
    /// Something on our side or the provider's went wrong — the exchange was rejected, the invite
    /// had gone, or the caregiver's access to the member had been withdrawn in the meantime.
    /// </summary>
    Failed = 3,
}

/// <summary>
/// The result of finishing a wearer-channel OAuth callback, in the terms the page that follows
/// needs to render.
/// </summary>
/// <remarks>
/// Carries a brand name at most. The wearer's page must be able to say "your Fitbit is connected"
/// and nothing further — no member id, no connection id, no account detail — because it is served
/// to whoever holds the link.
/// </remarks>
/// <param name="Result">What happened.</param>
/// <param name="DeviceDisplayName">The brand just connected, on <see cref="WearerConnectionResult.Completed"/>.</param>
/// <param name="CanRetry">
/// Whether the invite is still live, so the page can offer another go. False once the invite has
/// been spent or has expired, where a retry button would only lead to a dead end.
/// </param>
public sealed record WearerConnectionOutcome(
    WearerConnectionResult Result,
    string? DeviceDisplayName,
    bool CanRetry);
