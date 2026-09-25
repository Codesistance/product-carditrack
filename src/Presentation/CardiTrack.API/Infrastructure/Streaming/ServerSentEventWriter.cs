using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;

namespace CardiTrack.API.Infrastructure.Streaming;

/// <summary>
/// Writes <c>text/event-stream</c> events to a response, starting it lazily on the first event
/// so a caller can still answer with an ordinary status and JSON body until then — see
/// <see cref="Started"/>.
/// </summary>
/// <remarks>
/// Each event is <c>event: {name}</c> and one <c>data:</c> line of JSON, flushed at once.
/// System.Text.Json never writes a raw newline in its compact output (it escapes them in
/// strings), so a payload always fits the one data line the format needs.
/// </remarks>
public sealed class ServerSentEventWriter
{
    public const string ContentType = "text/event-stream";

    private static readonly byte[] Heartbeat = Encoding.UTF8.GetBytes(": keep-alive\n\n");

    private readonly HttpResponse _response;
    private readonly JsonSerializerOptions _json;

    public ServerSentEventWriter(HttpResponse response, JsonSerializerOptions json)
    {
        _response = response;
        // Compact output whatever the host configured: an indented payload would put newlines
        // inside the one data line an event carries.
        _json = json.WriteIndented ? new JsonSerializerOptions(json) { WriteIndented = false } : json;
    }

    /// <summary>
    /// True once the 200 and the event-stream headers have gone out. From then on the status is
    /// fixed, and a failure can only be reported as an event.
    /// </summary>
    public bool Started { get; private set; }

    public async Task WriteAsync<T>(string eventName, T payload, CancellationToken ct)
    {
        await StartAsync(ct);
        var data = JsonSerializer.Serialize(payload, _json);
        await _response.WriteAsync($"event: {eventName}\ndata: {data}\n\n", Encoding.UTF8, ct);
        await _response.Body.FlushAsync(ct);
    }

    /// <summary>A comment line: ignored by every event-stream reader, and bytes on the wire for
    /// anything between the two ends that closes idle connections.</summary>
    public async Task WriteHeartbeatAsync(CancellationToken ct)
    {
        await StartAsync(ct);
        await _response.Body.WriteAsync(Heartbeat, ct);
        await _response.Body.FlushAsync(ct);
    }

    private async Task StartAsync(CancellationToken ct)
    {
        if (Started)
            return;

        _response.StatusCode = StatusCodes.Status200OK;
        _response.ContentType = ContentType;
        _response.Headers.CacheControl = "no-cache";
        // Proxies that buffer by default (nginx and its kin) pass the stream through as written.
        _response.Headers["X-Accel-Buffering"] = "no";
        _response.HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        await _response.StartAsync(ct);
        Started = true;
    }
}
