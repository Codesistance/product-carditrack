using System.Net;
using System.Text;

namespace CardiTrack.UnitTests.Mobile;

/// <summary>
/// Scripted HttpMessageHandler: returns queued responses in order (repeating the last one)
/// and records each request's method, URI, and body for assertions.
/// </summary>
public sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly object _gate = new();
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();
    private readonly List<(HttpMethod Method, Uri? Uri, string? Body, string? AuthHeader)> _requests = [];
    private Func<HttpRequestMessage, HttpResponseMessage>? _last;

    public List<(HttpMethod Method, Uri? Uri, string? Body, string? AuthHeader)> Requests
    {
        get
        {
            lock (_gate)
                return [.. _requests];
        }
    }

    public FakeHttpMessageHandler Enqueue(HttpStatusCode status, string body, string mediaType = "application/json")
    {
        lock (_gate)
        {
            _responses.Enqueue(_ => new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, mediaType),
            });
        }

        return this;
    }

    public FakeHttpMessageHandler Enqueue(Func<HttpRequestMessage, HttpResponseMessage> factory)
    {
        lock (_gate)
            _responses.Enqueue(factory);
        return this;
    }

    public FakeHttpMessageHandler Throws(Exception exception)
    {
        lock (_gate)
            _responses.Enqueue(_ => throw exception);
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        Func<HttpRequestMessage, HttpResponseMessage> factory;
        lock (_gate)
        {
            _requests.Add((request.Method, request.RequestUri, body, request.Headers.Authorization?.ToString()));
            if (_responses.Count > 0)
                _last = _responses.Dequeue();
            if (_last is null)
                throw new InvalidOperationException("FakeHttpMessageHandler has no scripted response.");
            factory = _last;
        }

        return factory(request);
    }
}
