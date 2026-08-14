using System.Net.Http.Json;
using System.Text.Json;
using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.Interfaces.Clients;

namespace CardiTrack.Infrastructure.ExternalClients;

/// <summary>
/// HTTP adapter for <see cref="IAlertNotificationEnqueue"/>. Posts only the alert id — which
/// caregivers are notified is resolved server-side, never trusted from this caller.
/// </summary>
internal sealed class InternalNotificationEnqueueClient : IAlertNotificationEnqueue
{
    public const string HttpClientName = "InternalNotificationEnqueue";

    private const string EnqueuePath = "api/v1/internal/notifications/enqueue";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IHttpClientFactory _httpClientFactory;

    public InternalNotificationEnqueueClient(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task EnqueueForAlertAsync(Guid alertId, CancellationToken ct = default)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var response = await client.PostAsJsonAsync(
            EnqueuePath, new EnqueueDeliveryRequest { AlertId = alertId }, JsonOptions, ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Internal enqueue for Alert {alertId} returned {(int)response.StatusCode}.");
        }
    }
}
