namespace CardiTrack.Application.DTOs.Responses;

/// <summary>
/// Where an account stands with respect to deletion — what the app shows a caregiver who has
/// asked for their account to go, and what it shows everyone else.
/// </summary>
/// <param name="DeletionRequested">Whether a request is outstanding.</param>
/// <param name="RequestedAtUtc">When it was made. Null when none is outstanding.</param>
/// <param name="ScheduledForUtc">
/// When the data is due to be erased — 30 days after the request, matching the published policy.
/// Null when no request is outstanding.
/// </param>
/// <param name="CanCancel">
/// Whether signing in again would call the whole thing off. True for the whole window: the
/// promise is erasure <em>within</em> 30 days, and a caregiver who changes their mind on day 29
/// is exactly who the window is for.
/// </param>
public sealed record AccountDeletionStatusResponse(
    bool DeletionRequested,
    DateTime? RequestedAtUtc,
    DateTime? ScheduledForUtc,
    bool CanCancel);
