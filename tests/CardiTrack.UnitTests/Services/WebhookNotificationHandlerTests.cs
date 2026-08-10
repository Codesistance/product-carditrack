using CardiTrack.HealthWebhookReceiver;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// Pins the receiver's whole contract: authenticate the shared secret over the full
/// Authorization header, forward the raw body, 204 — and never acknowledge what was not kept.
/// </summary>
public class WebhookNotificationHandlerTests
{
    private const string Secret = "Bearer whsec_9f3a1c";

    private readonly INotificationPublisher _publisher = Substitute.For<INotificationPublisher>();
    private static readonly DateTime ReceivedAt = new(2026, 8, 10, 7, 0, 0, DateTimeKind.Utc);

    private WebhookNotificationHandler CreateSut() => new(Secret, _publisher);

    [Fact]
    public async Task AuthenticatedNotification_IsPublishedRaw_AndAcknowledged()
    {
        var status = await CreateSut().HandleAsync(
            Secret, """{"user":"users/abc","dataType":"heart-rate"}""", "application/json",
            ReceivedAt, CancellationToken.None);

        Assert.Equal(204, status);
        await _publisher.Received(1).PublishAsync(
            """{"user":"users/abc","dataType":"heart-rate"}""", "application/json", ReceivedAt,
            Arg.Any<CancellationToken>());
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
