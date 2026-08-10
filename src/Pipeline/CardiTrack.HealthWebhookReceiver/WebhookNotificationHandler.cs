using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

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
/// downstream ever has to trust this payload's shape, and this service never parses it. The one
/// sanctioned peek is the verification probe check below, whose shape Google documents.
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
        ArgumentNullException.ThrowIfNull(publisher);

        _secretUtf8 = Encoding.UTF8.GetBytes(secret);
        _publisher = publisher;
    }

    /// <summary>
    /// Returns the status code to answer with: 401 for anything unauthenticated (no detail — an
    /// unauthenticated caller learns nothing), 200 once the notification is on the stream.
    /// <para>
    /// 200 rather than 204, and exactly 401 on the unauthenticated side, because those are the
    /// answers Google's documented two-step verification handshake requires: an authorized
    /// probe must see <c>200 OK</c>/<c>201 Created</c>, an unauthorized one 401/403 — anything
    /// else fails Subscriber creation with <c>FAILED_PRECONDITION</c>. One status for probe and
    /// notification alike keeps a single path through this method.
    /// </para>
    /// A publish failure throws, which surfaces as a 5xx and lets Google retry — dropping the
    /// notification silently would ACK data we did not keep.
    /// </summary>
    public async Task<int> HandleAsync(
        string? authorizationHeader, string body, string? contentType, DateTime receivedAtUtc,
        CancellationToken ct)
    {
        if (!SecretMatches(authorizationHeader))
            return StatusCodes.Status401Unauthorized;

        // The handshake's authorized probe is acknowledged but never forwarded: it is Google
        // checking us, not a notification, and publishing it would seed the topic with a
        // poison message on every Subscriber create or endpoint update.
        if (!IsVerificationProbe(body))
            await _publisher.PublishAsync(body, contentType, receivedAtUtc, ct);

        return StatusCodes.Status200OK;
    }

    /// <summary>
    /// Whether the body is Google's verification probe — documented as a JSON object with
    /// <c>"type": "verification"</c> (User-Agent <c>Google-Health-API-Webhooks</c>, but the
    /// body is the contract we match on). Anything that does not parse to that shape is
    /// treated as a notification: when in doubt, forward — the drain's parser already
    /// tolerates unknown shapes, while a swallowed real notification would be a silent gap
    /// until the fallback poll.
    /// </summary>
    private static bool IsVerificationProbe(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("type", out var type)
                   && type.ValueKind == JsonValueKind.String
                   && type.ValueEquals("verification");
        }
        catch (JsonException)
        {
            return false;
        }
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
