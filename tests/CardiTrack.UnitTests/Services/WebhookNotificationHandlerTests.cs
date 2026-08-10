using CardiTrack.HealthWebhookReceiver;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// Pins the receiver's whole contract: authenticate the shared secret over the full
/// Authorization header, forward the raw body, 200 — and never acknowledge what was not kept.
/// The statuses are load-bearing: Google's documented verification handshake requires exactly
/// 200/201 for its authorized probe and 401/403 for its unauthorized one, or Subscriber
/// creation fails with FAILED_PRECONDITION.
/// </summary>
public class WebhookNotificationHandlerTests
{
    private const string Secret = "Bearer whsec_9f3a1c";

    private readonly INotificationPublisher _publisher = Substitute.For<INotificationPublisher>();
    private static readonly DateTime ReceivedAt = new(2026, 8, 10, 7, 0, 0, DateTimeKind.Utc);

    private WebhookNotificationHandler CreateSut() => new(Secret, _publisher);

    [Fact]
    public async Task AuthenticatedNotification_IsPublishedRaw_AndAcknowledgedWith200()
    {
        var status = await CreateSut().HandleAsync(
            Secret, """{"user":"users/abc","dataType":"heart-rate"}""", "application/json",
            ReceivedAt, CancellationToken.None);

        Assert.Equal(200, status);
        await _publisher.Received(1).PublishAsync(
            """{"user":"users/abc","dataType":"heart-rate"}""", "application/json", ReceivedAt,
            Arg.Any<CancellationToken>());
    }

    // The handshake's authorized probe gets its 200 but never reaches the topic — publishing
    // it would seed a poison message on every Subscriber create or endpoint update.
    [Theory]
    [InlineData("""{"type": "verification"}""")]
    [InlineData("""{"type":"verification"}""")]
    [InlineData("""  { "type" : "verification", "extra": 1 }  """)]
    public async Task TheVerificationProbe_Gets200_ButIsNeverForwarded(string body)
    {
        var status = await CreateSut().HandleAsync(
            body: body, authorizationHeader: Secret, contentType: "application/json",
            receivedAtUtc: ReceivedAt, ct: CancellationToken.None);

        Assert.Equal(200, status);
        await _publisher.DidNotReceive().PublishAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    // An UNAUTHORIZED probe must see 401 — that is the handshake's second step — and the
    // probe-shaped body earns it no exemption from authentication.
    [Fact]
    public async Task AnUnauthenticatedVerificationProbe_StillGets401()
    {
        var status = await CreateSut().HandleAsync(
            "Bearer wrong", """{"type": "verification"}""", "application/json",
            ReceivedAt, CancellationToken.None);

        Assert.Equal(401, status);
    }

    // When in doubt, forward: a body that merely resembles the probe — wrong type value, type
    // in prose, broken JSON — is treated as a notification, because a swallowed real
    // notification is a silent gap until the fallback poll.
    [Theory]
    [InlineData("""{"type": "notification"}""")]
    [InlineData("""{"kind": "verification"}""")]
    [InlineData("""["type", "verification"]""")]
    [InlineData("type: verification")]
    [InlineData("")]
    public async Task AnythingElse_IsForwardedAsANotification(string body)
    {
        var status = await CreateSut().HandleAsync(
            Secret, body, "application/json", ReceivedAt, CancellationToken.None);

        Assert.Equal(200, status);
        await _publisher.Received(1).PublishAsync(
            body, "application/json", ReceivedAt, Arg.Any<CancellationToken>());
    }

    // The whole header is the credential — a matching token under a different scheme, a prefix,
    // or an empty header must all fail identically.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Bearer wrong")]
    [InlineData("whsec_9f3a1c")]
    [InlineData("Bearer whsec_9f3a1c ")]
    [InlineData("Bearer whsec_9f3a1")]
    public async Task UnauthenticatedNotification_Gets401_AndIsNeverPublished(string? header)
    {
        var status = await CreateSut().HandleAsync(
            header, "{}", "application/json", ReceivedAt, CancellationToken.None);

        Assert.Equal(401, status);
        await _publisher.DidNotReceive().PublishAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    // ACKing a notification we failed to keep would tell Google not to retry data we dropped.
    [Fact]
    public async Task PublishFailure_Propagates_InsteadOfAcknowledging()
    {
        _publisher.PublishAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("topic unavailable"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateSut().HandleAsync(Secret, "{}", null, ReceivedAt, CancellationToken.None));
    }

    [Fact]
    public void AnEmptySecret_RefusesToConstruct()
    {
        Assert.Throws<ArgumentException>(() => new WebhookNotificationHandler("  ", _publisher));
    }
}
