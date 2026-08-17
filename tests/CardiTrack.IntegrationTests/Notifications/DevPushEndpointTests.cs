using System.Linq.Expressions;
using CardiTrack.API.Controllers.Dev;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Security;
using CardiTrack.Application.Services.Notifications;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace CardiTrack.IntegrationTests.Notifications;

/// <summary>
/// notification_engine.md §13. The endpoint exists to answer one question — did the push leave the
/// server, and if not, why — so the cases that matter are the three ways it returns 200 having
/// sent nothing, plus the token check standing in for authentication.
/// </summary>
public class DevPushEndpointTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 8, 17, 9, 0, 0, TimeSpan.Zero);
    private const string Key = "yLQb7f0m1Xy0k4V3z8Q9hJ2sN6pR5tW1cE4gU7aD0bM=";

    private readonly FixedTimeProvider _time = new(Now);
    private readonly IDispatchService _dispatch = Substitute.For<IDispatchService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly DevPushTokenService _tokens;

    public DevPushEndpointTests()
    {
        _tokens = new DevPushTokenService(Key, _time);
        _unitOfWork.NotificationDeliveries.Returns(Substitute.For<INotificationDeliveryRepository>());
        _unitOfWork.PushDeviceTokens.Returns(Substitute.For<IPushDeviceTokenRepository>());
    }

    // ── Authorization ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task MissingToken_Is401AndNeverEnqueues()
    {
        var result = await CreateSut(token: null).Send(Request(), CancellationToken.None);

        Assert.IsType<UnauthorizedObjectResult>(result.Result);
        await _dispatch.DidNotReceiveWithAnyArgs().EnqueueAsync(default!, default);
    }

    [Fact]
    public async Task TokenForADifferentUser_Is401()
    {
        var token = _tokens.Issue(Guid.NewGuid(), DeliveryCategory.Safety, null, Now.AddMinutes(2).UtcDateTime);

        var result = await CreateSut(token).Send(Request(), CancellationToken.None);

        Assert.IsType<UnauthorizedObjectResult>(result.Result);
        await _dispatch.DidNotReceiveWithAnyArgs().EnqueueAsync(default!, default);
    }

    [Fact]
    public async Task HealthWithoutSeverity_Is400()
    {
        var request = Request(DeliveryCategory.Health);
        var result = await CreateSut(TokenFor(request)).Send(request, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        await _dispatch.DidNotReceiveWithAnyArgs().EnqueueAsync(default!, default);
    }

    // ── The enqueue itself ────────────────────────────────────────────────────────

    [Fact]
    public async Task ValidToken_EnqueuesThroughTheRealOutboxWithNoCollapseKey()
    {
        var request = Request();
        Enqueues(PushDelivery());

        await CreateSut(TokenFor(request)).Send(request, CancellationToken.None);

        await _dispatch.Received(1).EnqueueAsync(
            Arg.Is<EnqueueRequest>(r =>
                r.UserId == UserId
                && r.Category == DeliveryCategory.Safety
                && r.SourceType == DeliverySourceType.Notification
                && r.SourceId == Guid.Empty
                // A collapse key would let a repeated test push replace the previous one on the
                // device, hiding the very thing being observed.
                && r.CollapseKey == null
                && r.DedupKey.StartsWith("devpush:")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TwoPushesInTheSameSecond_GetDistinctDedupKeys()
    {
        // Sub-second resolution is the whole point: DispatchService returns the existing row
        // unsent on a dedup hit, which is indistinguishable from a broken push.
        var request = Request();
        var keys = new List<string>();
        _dispatch.EnqueueAsync(Arg.Do<EnqueueRequest>(r => keys.Add(r.DedupKey)), Arg.Any<CancellationToken>())
            .Returns(PushDelivery());

        var sut = CreateSut(TokenFor(request));
        await sut.Send(request, CancellationToken.None);
        _time.Advance(TimeSpan.FromMilliseconds(1));
        await sut.Send(request, CancellationToken.None);

        Assert.Equal(2, keys.Distinct().Count());
    }

    [Fact]
    public async Task Response_ReportsTheChannelIdAndSoundsThePayloadCarries()
    {
        var request = Request();
        Enqueues(PushDelivery());

        var response = await SendAndUnwrap(request);

        Assert.Equal(NotificationChannels.Safety, response.AndroidChannelId);
        Assert.Equal(NotificationChannels.AlertSound, response.AndroidSound);
        Assert.Equal(NotificationChannels.AlertSoundFile, response.IosSound);
    }

    // ── The three silent 200s ─────────────────────────────────────────────────────

    [Fact]
    public async Task NoLiveDeviceToken_ExplainsWhyNothingArrived()
    {
        var request = Request();
        Enqueues(PushDelivery());   // Pending, no PushDeviceTokenId — DispatchService found no devices

        var response = await SendAndUnwrap(request);

        Assert.Empty(response.Devices);
        Assert.Contains("No live device token", response.Hint);
    }

    [Fact]
    public async Task NudgeCategory_SaysItWasNeverGoingToPush()
    {
        var request = Request(DeliveryCategory.Nudge);
        var delivery = PushDelivery();
        delivery.Channel = DeliveryChannel.InApp;
        delivery.Category = DeliveryCategory.Nudge;
        Enqueues(delivery);

        var response = await SendAndUnwrap(request);

        Assert.Equal(DeliveryChannel.InApp, response.Channel);
        Assert.Contains("Nudge deliveries never push", response.Hint);
    }

    [Fact]
    public async Task QuietHoursDeferral_SaysNothingHasBeenSentYet()
    {
        var request = Request(DeliveryCategory.Health, AlertSeverity.Orange);
        var delivery = PushDelivery();
        delivery.ScheduledFor = Now.AddHours(6).UtcDateTime;
        Enqueues(delivery);

        var response = await SendAndUnwrap(request);

        Assert.Contains("Quiet hours deferred", response.Hint);
    }

    // ── A clean send ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task SentToADevice_ReportsThatDeviceAndNoHint()
    {
        var request = Request();
        var tokenId = Guid.NewGuid();
        var delivery = PushDelivery();
        delivery.State = DeliveryState.Sent;
        delivery.PushDeviceTokenId = tokenId;
        delivery.ProviderMessageId = "projects/carditrack/messages/1234";
        Enqueues(delivery);

        _unitOfWork.PushDeviceTokens.GetByIdAsync(tokenId).Returns(new PushDeviceToken
        {
            Id = tokenId,
            UserId = UserId,
            DeviceId = "emulator-5554",
            Platform = DevicePlatform.Android,
            AppVersion = "0.2.163",
            TokenFingerprint = "a3f19c4d5e6f7a8b9c0d1e2f",
            OsAuthorizationStatus = OsAuthorizationStatus.Granted,
            SafetyChannelEnabled = true
        });

        var response = await SendAndUnwrap(request);

        Assert.Null(response.Hint);
        var device = Assert.Single(response.Devices);
        Assert.Equal("emulator-5554", device.DeviceId);
        Assert.Equal(DeliveryState.Sent, device.State);
        Assert.Equal("projects/carditrack/messages/1234", device.ProviderMessageId);
    }

    [Fact]
    public async Task DeviceFingerprint_IsTruncatedNeverWhole()
    {
        // The fingerprint identifies a device across reinstalls — Tier 1 data. Six characters is
        // enough to tell two emulators apart in a log and not enough to correlate anything.
        var request = Request();
        var tokenId = Guid.NewGuid();
        var delivery = PushDelivery();
        delivery.PushDeviceTokenId = tokenId;
        Enqueues(delivery);

        const string fullFingerprint = "a3f19c4d5e6f7a8b9c0d1e2f3a4b5c6d7e8f9a0b1c2d3e4f5a6b7c8d9e0f1a2b";
        _unitOfWork.PushDeviceTokens.GetByIdAsync(tokenId).Returns(new PushDeviceToken
        {
            Id = tokenId,
            UserId = UserId,
            DeviceId = "emulator-5554",
            TokenFingerprint = fullFingerprint
        });

        var response = await SendAndUnwrap(request);

        Assert.Equal("a3f19c", Assert.Single(response.Devices).Fingerprint);
        Assert.DoesNotContain(fullFingerprint, System.Text.Json.JsonSerializer.Serialize(response));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────

    private static DevPushRequest Request(
        DeliveryCategory category = DeliveryCategory.Safety, AlertSeverity? severity = null) =>
        new() { UserId = UserId, Category = category, Severity = severity };

    private string TokenFor(DevPushRequest request) =>
        _tokens.Issue(request.UserId, request.Category, request.Severity, Now.AddMinutes(2).UtcDateTime);

    private static NotificationDelivery PushDelivery() => new()
    {
        UserId = UserId,
        Category = DeliveryCategory.Safety,
        Channel = DeliveryChannel.Push,
        State = DeliveryState.Pending,
        DedupKey = "unused",
        ExpiresAt = Now.AddMinutes(30).UtcDateTime
    };

    private void Enqueues(NotificationDelivery delivery)
    {
        _dispatch.EnqueueAsync(Arg.Any<EnqueueRequest>(), Arg.Any<CancellationToken>()).Returns(delivery);
        _unitOfWork.NotificationDeliveries
            .FindAsync(Arg.Any<Expression<Func<NotificationDelivery, bool>>>())
            .Returns([delivery]);
    }

    private async Task<DevPushResponse> SendAndUnwrap(DevPushRequest request)
    {
        var result = await CreateSut(TokenFor(request)).Send(request, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var envelope = Assert.IsType<ApiResponse<DevPushResponse>>(ok.Value);
        Assert.NotNull(envelope.Data);
        return envelope.Data;
    }

    private DevPushController CreateSut(string? token)
    {
        var sut = new DevPushController(
            _dispatch, _tokens, _unitOfWork, NullLogger<DevPushController>.Instance, _time)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        if (token is not null)
            sut.ControllerContext.HttpContext.Request.Headers[DevPushController.TokenHeader] = token;

        return sut;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }
}
