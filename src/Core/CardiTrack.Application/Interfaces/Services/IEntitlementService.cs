using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Interfaces.Services;

/// <summary>
/// Single authority for "does this organisation's plan include this feature?".
/// </summary>
/// <remarks>
/// <para>
/// The first entitlement check in the codebase: until now <c>SubscriptionService</c> only ever
/// created trials, and nothing read <see cref="Domain.Entities.Subscription.Tier"/> back. It is a
/// service rather than an inline tier comparison so the next gate — the tier member limit the
/// release matrix records as unenforced — has somewhere to go, and so "which plan buys what" is
/// answered in one file instead of drifting across controllers.
/// </para>
/// <para>
/// Deliberately separate from <see cref="ICardiMemberAccessService"/>, and never a substitute for
/// it. Access answers "may this person see this health data" and denies as 404, because confirming
/// a member exists is itself a disclosure. Entitlement answers "is this feature on their plan" and
/// denies openly — the caregiver is meant to know what upgrading would buy them.
/// </para>
/// </remarks>
public interface IEntitlementService
{
    /// <summary>
    /// Throws <see cref="FeatureNotEntitledException"/> unless the organisation's plan includes
    /// the feature.
    /// </summary>
    Task RequireAsync(Guid organizationId, PlanFeature feature, CancellationToken ct = default);

    /// <summary>
    /// Whether the organisation's plan includes the feature. For surfaces that shape themselves
    /// around the plan — the mobile export entry point offers the upsell instead of a form that
    /// would be refused after it is filled in.
    /// </summary>
    Task<bool> HasAsync(Guid organizationId, PlanFeature feature, CancellationToken ct = default);
}

/// <summary>
/// A capability a plan may or may not include. One member today; the tier member limit and the
/// device-type restrictions in <c>Subscription.Features</c> are the obvious next entries.
/// </summary>
public enum PlanFeature
{
    /// <summary>
    /// Health data export in any format. Complete Care and above — Basic has no export at all
    /// (docs/release_matrix.md, "Export plan-gating").
    /// </summary>
    HealthDataExport = 1
}

/// <summary>
/// The organisation's plan does not include the requested feature. Carries the tier needed so the
/// API can name it in the upsell rather than saying only "no".
/// </summary>
public class FeatureNotEntitledException : Exception
{
    public FeatureNotEntitledException(PlanFeature feature, SubscriptionTier requiredTier, string message)
        : base(message)
    {
        Feature = feature;
        RequiredTier = requiredTier;
    }

    public PlanFeature Feature { get; }

    public SubscriptionTier RequiredTier { get; }
}
