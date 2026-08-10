namespace CardiTrack.Application.Interfaces.Services;

/// <summary>
/// Re-runs gap detection immediately after a write that may have closed one, so a caregiver who
/// taps "add an emergency contact" and saves sees the card clear before the screen pops.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately a direct service call rather than a domain event. This solution has no event
/// infrastructure — no mediator, no dispatcher — and introducing one for a single feature would
/// leave one corner of the codebase on a pattern nothing else uses. Services already compose
/// through Application ports this way; <c>OnboardingService</c> injects <c>ISubscriptionService</c>
/// for the same reason.
/// </para>
/// <para>
/// The detection worker remains the backstop. Missing a call site here delays a resolution to the
/// next run; it does not corrupt anything, because reconciliation converges either way.
/// </para>
/// </remarks>
public interface INotificationGapResolver
{
    Task ResolveForCardiMemberAsync(Guid cardiMemberId, CancellationToken ct = default);

    Task ResolveForUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Withdraws every live notification about a member — used when monitoring is paused or the
    /// member is removed, where the gaps have not been fixed so much as stopped applying.
    /// </summary>
    Task WithdrawForCardiMemberAsync(
        Guid cardiMemberId, Domain.Enums.NotificationResolutionReason reason, CancellationToken ct = default);
}
