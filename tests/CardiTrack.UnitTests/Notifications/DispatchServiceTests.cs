using CardiTrack.Application.Interfaces.Clients;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Services.Notifications;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using NSubstitute;

namespace CardiTrack.UnitTests.Notifications;

/// <summary>
/// Pins the two escalation-adjacent bugs found in PR #183 review round 1: the 120s repush must
/// actually resend (not no-op on a <c>Sent</c> row), and resending must never reset the escalation
/// clock's origin (<c>SentDate</c>) or skip the first backoff step.
/// </summary>
public class DispatchServiceTests
{
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly INotificationChannel _channel = Substitute.For<INotificationChannel>();
    private readonly INotificationPreferenceService _preferences = Substitute.For<INotificationPreferenceService>();
    private readonly INotificationGapResolver _gapResolver = Substitute.For<INotificationGapResolver>();
    private readonly IPushDeviceTokenRepository _tokens = Substitute.For<IPushDeviceTokenRepository>();
    private readonly INotificationDeliveryRepository _deliveries = Substitute.For<INotificationDeliveryRepository>();
    private readonly IMemberQuestionnaireRepository _questionnaires = Substitute.For<IMemberQuestionnaireRepository>();
    private readonly IUserCardiMemberRepository _links = Substitute.For<IUserCardiMemberRepository>();

    private readonly FixedTimeProvider _timeProvider = new(new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero));
    private readonly Guid _tokenId = Guid.NewGuid();

    public DispatchServiceTests()
    {
        _unitOfWork.PushDeviceTokens.Returns(_tokens);
        _unitOfWork.NotificationDeliveries.Returns(_deliveries);
        _unitOfWork.MemberQuestionnaires.Returns(_questionnaires);
        _unitOfWork.UserCardiMembers.Returns(_links);
        _tokens.GetByIdAsync(_tokenId).Returns(new PushDeviceToken
        {
            Id = _tokenId,
            UserId = Guid.NewGuid(),
            DeviceId = "device-1",
            Platform = DevicePlatform.Ios,
            Token = "ciphertext"
        });
    }

    private DispatchService CreateSut() =>
        new(_unitOfWork, _channel, _preferences, _gapResolver, _timeProvider);

    private static NotificationDelivery SentDelivery(Guid tokenId, DateTime originalSentDate) => new()
    {
        Id = Guid.NewGuid(),
        SourceType = DeliverySourceType.Alert,
        SourceId = Guid.NewGuid(),
        UserId = Guid.NewGuid(),
        Category = DeliveryCategory.Safety,
        Channel = DeliveryChannel.Push,
        State = DeliveryState.Sent,
        PushDeviceTokenId = tokenId,
        DedupKey = $"test:{Guid.NewGuid()}",
        ExpiresAt = originalSentDate.AddMinutes(30),
        SentDate = originalSentDate,
        EscalationStage = EscalationStage.Initial
    };

    [Fact]
    public async Task RetryClaimedAsync_OnASentDelivery_ActuallyRepushes()
    {
        // This is exactly what NotificationDispatchWorker's escalation sweep does at the 120s
        // boundary — calls RetryClaimedAsync on a still-Sent row. Before this fix it no-opped.
        var delivery = SentDelivery(_tokenId, _timeProvider.GetUtcNow().UtcDateTime.AddSeconds(-125));
        _channel.SendAsync(delivery, Arg.Any<PushDeviceToken>(), Arg.Any<CancellationToken>())
            .Returns(new SendResult.Sent("provider-msg-2"));

        await CreateSut().RetryClaimedAsync(delivery);

        await _channel.Received(1).SendAsync(delivery, Arg.Any<PushDeviceToken>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RetryClaimedAsync_RepushSucceeds_DoesNotResetSentDate()
    {
        // The escalation ladder's 300s/900s boundaries are elapsed time since the ORIGINAL send.
        // Overwriting SentDate on every successful repush would restart that clock and those
        // later stages would never fire.
        var originalSentDate = _timeProvider.GetUtcNow().UtcDateTime.AddSeconds(-125);
        var delivery = SentDelivery(_tokenId, originalSentDate);
        _channel.SendAsync(delivery, Arg.Any<PushDeviceToken>(), Arg.Any<CancellationToken>())
            .Returns(new SendResult.Sent("provider-msg-2"));

        await CreateSut().RetryClaimedAsync(delivery);

        Assert.Equal(originalSentDate, delivery.SentDate);
    }

    [Fact]
    public async Task RetryClaimedAsync_FirstRetryableFailure_SchedulesTheFirstBackoffStep_Not15sSkipped()
    {
        // RetryBackoffPolicy's schedule is 15s/60s/5m/30m/2h, indexed by the PRE-increment
        // Attempts count (0 => 15s). Incrementing before computing the delay would skip straight
        // to 60s on the very first failure.
        var delivery = SentDelivery(_tokenId, _timeProvider.GetUtcNow().UtcDateTime.AddSeconds(-125));
        delivery.Attempts = 0;
        _channel.SendAsync(delivery, Arg.Any<PushDeviceToken>(), Arg.Any<CancellationToken>())
            .Returns(new SendResult.Retryable("transport-error"));

        await CreateSut().RetryClaimedAsync(delivery);

        Assert.Equal(1, delivery.Attempts);
        Assert.Equal(_timeProvider.GetUtcNow().UtcDateTime + TimeSpan.FromSeconds(15), delivery.NextAttemptAt);
    }

    [Fact]
    public async Task RetryClaimedAsync_SecondRetryableFailure_SchedulesTheSecondBackoffStep()
    {
        var delivery = SentDelivery(_tokenId, _timeProvider.GetUtcNow().UtcDateTime.AddSeconds(-125));
        delivery.Attempts = 1;
        _channel.SendAsync(delivery, Arg.Any<PushDeviceToken>(), Arg.Any<CancellationToken>())
            .Returns(new SendResult.Retryable("transport-error"));

        await CreateSut().RetryClaimedAsync(delivery);

        Assert.Equal(2, delivery.Attempts);
        Assert.Equal(_timeProvider.GetUtcNow().UtcDateTime + TimeSpan.FromMinutes(1), delivery.NextAttemptAt);
    }

    [Fact]
    public async Task RetryClaimedAsync_DeliveredRow_NeverRetried()
    {
        var delivery = SentDelivery(_tokenId, _timeProvider.GetUtcNow().UtcDateTime.AddSeconds(-125));
        delivery.State = DeliveryState.Delivered;

        await CreateSut().RetryClaimedAsync(delivery);

        await _channel.DidNotReceiveWithAnyArgs().SendAsync(default!, default!, default);
    }

    // ── EnqueueForQuestionnaireAsync ────────────────────────────────────────

    private static MemberQuestionnaire Questionnaire(
        QuestionnaireStatus status = QuestionnaireStatus.Pending, DateTime? askableUntilUtc = null) => new()
        {
            Id = Guid.NewGuid(),
            CardiMemberId = Guid.NewGuid(),
            QuestionText = "Has anything changed at home recently?",
            Status = status,
            GeneratedAtUtc = DateTime.UtcNow,
            AskableUntilUtc = askableUntilUtc,
        };

    private static UserCardiMember Caregiver(Guid cardiMemberId, bool receiveAlerts = true, bool isActive = true) => new()
    {
        UserId = Guid.NewGuid(),
        CardiMemberId = cardiMemberId,
        IsActive = isActive,
        ReceiveAlerts = receiveAlerts,
    };

    [Fact]
    public async Task EnqueueForQuestionnaireAsync_DispatchesToEveryCaregiverWithReceiveAlerts()
    {
        var questionnaire = Questionnaire();
        var wanted = Caregiver(questionnaire.CardiMemberId);
        var optedOut = Caregiver(questionnaire.CardiMemberId, receiveAlerts: false);
        var inactive = Caregiver(questionnaire.CardiMemberId, isActive: false);

        _questionnaires.GetByIdAsync(questionnaire.Id).Returns(questionnaire);
        _links.GetByCardiMemberIdAsync(questionnaire.CardiMemberId)
            .Returns([wanted, optedOut, inactive]);

        var results = await CreateSut().EnqueueForQuestionnaireAsync(questionnaire.Id, occurrence: 0);

        var delivery = Assert.Single(results);
        Assert.Equal(wanted.UserId, delivery.UserId);
        Assert.Equal(DeliverySourceType.Questionnaire, delivery.SourceType);
        Assert.Equal(DeliveryCategory.Questionnaire, delivery.Category);
    }

    [Fact]
    public async Task EnqueueForQuestionnaireAsync_ReturnsEmpty_WhenNoCaregiverReceivesAlerts()
    {
        var questionnaire = Questionnaire();
        _questionnaires.GetByIdAsync(questionnaire.Id).Returns(questionnaire);
        _links.GetByCardiMemberIdAsync(questionnaire.CardiMemberId)
            .Returns([Caregiver(questionnaire.CardiMemberId, receiveAlerts: false)]);

        var results = await CreateSut().EnqueueForQuestionnaireAsync(questionnaire.Id, occurrence: 0);

        Assert.Empty(results);
    }

    /// <summary>
    /// This is a shared entry point, not one only QuestionnaireAlertWorker's guarded claim can
    /// reach — a caller that skips the claim (or one that races it) must not still get a push for
    /// a question that has already been answered.
    /// </summary>
    [Fact]
    public async Task EnqueueForQuestionnaireAsync_ReturnsEmpty_WhenNoLongerPending()
    {
        var questionnaire = Questionnaire(QuestionnaireStatus.Answered);
        _questionnaires.GetByIdAsync(questionnaire.Id).Returns(questionnaire);
        _links.GetByCardiMemberIdAsync(questionnaire.CardiMemberId)
            .Returns([Caregiver(questionnaire.CardiMemberId)]);

        var results = await CreateSut().EnqueueForQuestionnaireAsync(questionnaire.Id, occurrence: 0);

        Assert.Empty(results);
        await _links.DidNotReceiveWithAnyArgs().GetByCardiMemberIdAsync(default);
    }

    [Fact]
    public async Task EnqueueForQuestionnaireAsync_ReturnsEmpty_WhenLapsed()
    {
        var questionnaire = Questionnaire(
            askableUntilUtc: _timeProvider.GetUtcNow().UtcDateTime.AddMinutes(-1));
        _questionnaires.GetByIdAsync(questionnaire.Id).Returns(questionnaire);

        var results = await CreateSut().EnqueueForQuestionnaireAsync(questionnaire.Id, occurrence: 0);

        Assert.Empty(results);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
