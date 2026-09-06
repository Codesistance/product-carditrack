using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Services;

/// <inheritdoc cref="IEntitlementService"/>
public class EntitlementService : IEntitlementService
{
    /// <summary>
    /// The minimum tier each feature needs. Tiers are ordered by their enum value —
    /// Basic(1) &lt; Complete(2) &lt; Plus(3) — so a higher plan always includes a lower plan's
    /// features without every entry having to list them.
    /// </summary>
    private static readonly Dictionary<PlanFeature, SubscriptionTier> Minimums = new()
    {
        [PlanFeature.HealthDataExport] = SubscriptionTier.Complete
    };

    /// <summary>
    /// Plan states that still buy things. Trial counts — a trial of Complete Care that could not
    /// export would not be a trial of Complete Care. Past-due does too, deliberately: dunning is a
    /// billing conversation, and cutting a caregiver off from their own health records over a
    /// failed card is the wrong lever. Cancelled and suspended do not.
    /// </summary>
    private static readonly SubscriptionStatus[] EntitlingStatuses =
    [
        SubscriptionStatus.Trial, SubscriptionStatus.Active, SubscriptionStatus.PastDue
    ];

    private readonly IUnitOfWork _unitOfWork;

    public EntitlementService(IUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    public async Task RequireAsync(Guid organizationId, PlanFeature feature, CancellationToken ct = default)
    {
        if (await HasAsync(organizationId, feature, ct))
            return;

        var required = MinimumTierFor(feature);
        throw new FeatureNotEntitledException(
            feature,
            required,
            $"Health data export is part of {DisplayName(required)} Care. "
            + "Upgrade your plan to export your family's health records.");
    }

    public async Task<bool> HasAsync(Guid organizationId, PlanFeature feature, CancellationToken ct = default)
    {
        var subscription = await _unitOfWork.Subscriptions.GetByOrganizationIdAsync(organizationId);

        // No subscription row at all is not entitlement by default. Every organisation gets a
        // trial at onboarding, so its absence means something went wrong — and a gate that opens
        // when it cannot find the plan is not a gate.
        if (subscription is null)
            return false;

        if (!EntitlingStatuses.Contains(subscription.Status))
            return false;

        // A trial that has run out stops entitling even before anything flips its status: the
        // status change is a scheduled job's work, and the date is the truth.
        if (subscription.Status == SubscriptionStatus.Trial && HasTrialLapsed(subscription))
            return false;

        return subscription.Tier >= MinimumTierFor(feature);
    }

    /// <summary>
    /// The tier a feature needs, or a fault that names the feature.
    /// </summary>
    /// <remarks>
    /// Indexing <see cref="Minimums"/> directly threw a bare <see cref="KeyNotFoundException"/> if
    /// a new <see cref="PlanFeature"/> were added without a row here — a 500 whose message names
    /// neither the feature nor this file. It throws rather than defaulting either way on purpose:
    /// guessing "Basic" would silently open a paid feature to everyone, and guessing the highest
    /// tier would silently withhold one, and both of those ship. This fails in the first minute of
    /// development instead.
    /// </remarks>
    private static SubscriptionTier MinimumTierFor(PlanFeature feature) =>
        Minimums.TryGetValue(feature, out var tier)
            ? tier
            : throw new ArgumentOutOfRangeException(
                nameof(feature), feature,
                $"No minimum subscription tier is configured for {feature}. "
                + $"Add it to {nameof(EntitlementService)}.{nameof(Minimums)}.");

    private static bool HasTrialLapsed(Subscription subscription) =>
        subscription.TrialEndDate is { } endsAt && endsAt <= DateTime.UtcNow;

    private static string DisplayName(SubscriptionTier tier) => tier.ToString();
}
