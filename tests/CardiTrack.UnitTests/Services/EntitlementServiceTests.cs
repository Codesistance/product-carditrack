using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using NSubstitute;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// The first entitlement check in the codebase. Export is gated to Complete Care
/// (docs/release_matrix.md, "Export plan-gating"), and the failure that matters most here is the
/// gate opening when it should not — so the tests lean on the refusal cases.
/// </summary>
public class EntitlementServiceTests
{
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly ISubscriptionRepository _subscriptions = Substitute.For<ISubscriptionRepository>();
    private readonly Guid _organizationId = Guid.NewGuid();

    public EntitlementServiceTests()
    {
        _unitOfWork.Subscriptions.Returns(_subscriptions);
    }

    private EntitlementService CreateSut() => new(_unitOfWork);

    private void GivenSubscription(
        SubscriptionTier tier,
        SubscriptionStatus status = SubscriptionStatus.Active,
        DateTime? trialEndDate = null) =>
        _subscriptions.GetByOrganizationIdAsync(_organizationId).Returns(new Subscription
        {
            OrganizationId = _organizationId,
            Tier = tier,
            Status = status,
            TrialEndDate = trialEndDate
        });

    [Theory]
    [InlineData(SubscriptionTier.Complete)]
    [InlineData(SubscriptionTier.Plus)]
    public async Task Export_IsAllowed_OnCompleteAndAbove(SubscriptionTier tier)
    {
        GivenSubscription(tier);

        Assert.True(await CreateSut().HasAsync(_organizationId, PlanFeature.HealthDataExport));
    }

    [Fact]
    public async Task Export_IsRefused_OnBasic()
    {
        GivenSubscription(SubscriptionTier.Basic);

        Assert.False(await CreateSut().HasAsync(_organizationId, PlanFeature.HealthDataExport));
    }

    [Fact]
    public async Task Export_IsAllowed_DuringAnActiveTrial()
    {
        // A trial of Complete Care that could not export would not be a trial of Complete Care.
        GivenSubscription(
            SubscriptionTier.Complete, SubscriptionStatus.Trial, DateTime.UtcNow.AddDays(10));

        Assert.True(await CreateSut().HasAsync(_organizationId, PlanFeature.HealthDataExport));
    }

    [Fact]
    public async Task Export_IsRefused_OnceTheTrialDateHasPassed()
    {
        // The status flip is a scheduled job's work; the date is the truth in the meantime.
        GivenSubscription(
            SubscriptionTier.Complete, SubscriptionStatus.Trial, DateTime.UtcNow.AddMinutes(-1));

        Assert.False(await CreateSut().HasAsync(_organizationId, PlanFeature.HealthDataExport));
    }

    [Fact]
    public async Task Export_IsAllowed_WhileBillingIsPastDue()
    {
        // Dunning is a billing conversation. Cutting a caregiver off from their own family's
        // health records over a failed card is the wrong lever to pull.
        GivenSubscription(SubscriptionTier.Complete, SubscriptionStatus.PastDue);

        Assert.True(await CreateSut().HasAsync(_organizationId, PlanFeature.HealthDataExport));
    }

    [Theory]
    [InlineData(SubscriptionStatus.Cancelled)]
    [InlineData(SubscriptionStatus.Suspended)]
    public async Task Export_IsRefused_OnANonEntitlingStatus(SubscriptionStatus status)
    {
        GivenSubscription(SubscriptionTier.Complete, status);

        Assert.False(await CreateSut().HasAsync(_organizationId, PlanFeature.HealthDataExport));
    }

    [Fact]
    public async Task Export_IsRefused_WhenNoSubscriptionExists()
    {
        // Every organisation gets a trial at onboarding, so a missing row means something went
        // wrong — and a gate that opens when it cannot find the plan is not a gate.
        _subscriptions.GetByOrganizationIdAsync(_organizationId).Returns((Subscription?)null);

        Assert.False(await CreateSut().HasAsync(_organizationId, PlanFeature.HealthDataExport));
    }

    [Fact]
    public async Task RequireAsync_Returns_WhenEntitled()
    {
        GivenSubscription(SubscriptionTier.Complete);

        var exception = await Record.ExceptionAsync(() =>
            CreateSut().RequireAsync(_organizationId, PlanFeature.HealthDataExport));

        Assert.Null(exception);
    }

    [Fact]
    public async Task RequireAsync_NamesTheTierNeeded_SoTheApiCanOfferTheUpgrade()
    {
        GivenSubscription(SubscriptionTier.Basic);

        var exception = await Assert.ThrowsAsync<FeatureNotEntitledException>(() =>
            CreateSut().RequireAsync(_organizationId, PlanFeature.HealthDataExport));

        Assert.Equal(SubscriptionTier.Complete, exception.RequiredTier);
        Assert.Equal(PlanFeature.HealthDataExport, exception.Feature);
        Assert.Contains("Complete", exception.Message);
    }
}
