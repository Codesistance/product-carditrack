using System.Net;
using System.Text;

namespace CardiTrack.UnitTests.Mobile;

/// <summary>
/// Scripted HttpMessageHandler: returns queued responses in order (repeating the last one)
/// and records each request's method, URI, and body for assertions.
/// </summary>
public sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();
    private Func<HttpRequestMessage, HttpResponseMessage>? _last;

    public List<(HttpMethod Method, Uri? Uri, string? Body, string? AuthHeader)> Requests { get; } = new();

    public FakeHttpMessageHandler Enqueue(HttpStatusCode status, string body, string mediaType = "application/json")
    {
        _responses.Enqueue(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, mediaType),
        });
        return this;
    }

    public FakeHttpMessageHandler Enqueue(Func<HttpRequestMessage, HttpResponseMessage> factory)
    {
        _responses.Enqueue(factory);
        return this;
    }

    public FakeHttpMessageHandler Throws(Exception exception)
    {
        _responses.Enqueue(_ => throw exception);
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        Requests.Add((request.Method, request.RequestUri, body, request.Headers.Authorization?.ToString()));

        if (_responses.Count > 0)
            _last = _responses.Dequeue();
        if (_last is null)
            throw new InvalidOperationException("FakeHttpMessageHandler has no scripted response.");
        return _last(request);
    }
}
