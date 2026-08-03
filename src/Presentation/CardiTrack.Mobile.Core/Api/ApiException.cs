using System.Net;

namespace CardiTrack.Mobile.Core.Api;

public sealed class ApiException : Exception
{
    public HttpStatusCode StatusCode { get; }
    public IReadOnlyList<string> Errors { get; }
    public bool IsSessionExpired => StatusCode == HttpStatusCode.Unauthorized;

    public ApiException(HttpStatusCode statusCode, string message, IReadOnlyList<string>? errors = null,
        Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
        Errors = errors ?? [];
    }
}
