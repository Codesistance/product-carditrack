using System.Net;

namespace CardiTrack.Mobile.Core.Api;

public sealed class ApiException : Exception
{
    public HttpStatusCode StatusCode { get; }
    public IReadOnlyList<string> Errors { get; }
    public bool IsSessionExpired => StatusCode == HttpStatusCode.Unauthorized;
    public bool IsNotFound => StatusCode == HttpStatusCode.NotFound;

    /// <summary>
    /// Transport failure (no route to the API), as opposed to an HTTP error the server
    /// returned. Callers that can show cached data key off this rather than the message.
    /// </summary>
    public bool IsNetworkFailure =>
        StatusCode == HttpStatusCode.ServiceUnavailable && InnerException is not null;

    public ApiException(HttpStatusCode statusCode, string message, IReadOnlyList<string>? errors = null,
        Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
        Errors = errors ?? [];
    }
}
