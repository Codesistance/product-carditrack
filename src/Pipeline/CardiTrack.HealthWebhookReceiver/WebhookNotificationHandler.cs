using System.Security.Cryptography;
using System.Text;

namespace CardiTrack.HealthWebhookReceiver;

/// <summary>Forwards an authenticated notification onto the realtime stream.</summary>
public interface INotificationPublisher
{
    Task PublishAsync(string body, string? contentType, DateTime receivedAtUtc, CancellationToken ct);
}

/// <summary>
/// The whole job of the receiver, kept out of Program.cs so it is unit-testable: authenticate the
/// shared secret, forward the raw body, acknowledge. The notification payload is deliberately
/// treated as opaque — the pipeline is notify-then-fetch (docs/llm_design.md), so nothing
/// downstream ever has to trust this payload's shape, and this service never parses it.
/// </summary>
public sealed class WebhookNotificationHandler
{
    private readonly byte[] _secretUtf8;
    private readonly INotificationPublisher _publisher;

    /// <param name="secret">
    /// The full Authorization header value registered in the Subscriber's
    /// endpointAuthorization.secret — scheme included, compared against the whole header.
    /// </param>
    public WebhookNotificationHandler(string secret, INotificationPublisher publisher)
    {
        if (string.IsNullOrWhiteSpace(secret))
            throw new ArgumentException("Webhook secret must be configured.", nameof(secret));

        _secretUtf8 = Encoding.UTF8.GetBytes(secret);
        _publisher = publisher;
    }

    /// <summary>
    /// Returns the status code to answer with: 401 for anything unauthenticated (no detail — an
    /// unauthenticated caller learns nothing), 204 once the notification is on the stream.
    /// A publish failure throws, which surfaces as a 5xx and lets Google retry — dropping the
    /// notification silently would ACK data we did not keep.
    /// </summary>
    public async Task<int> HandleAsync(
        string? authorizationHeader, string body, string? contentType, DateTime receivedAtUtc,
        CancellationToken ct)
    {
        if (!SecretMatches(authorizationHeader))
            return StatusCodes.Status401Unauthorized;

        await _publisher.PublishAsync(body, contentType, receivedAtUtc, ct);
        return StatusCodes.Status204NoContent;
    }

    /// <summary>
    /// Constant-time over the whole header. Length still leaks through FixedTimeEquals's
    /// length check; the secret is high-entropy, so that discloses nothing useful.
    /// </summary>
    private bool SecretMatches(string? authorizationHeader)
    {
        if (string.IsNullOrEmpty(authorizationHeader))
            return false;

        var headerUtf8 = Encoding.UTF8.GetBytes(authorizationHeader);
        return CryptographicOperations.FixedTimeEquals(headerUtf8, _secretUtf8);
    }
}
