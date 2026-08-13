namespace CardiTrack.Infrastructure.ExternalClients;

public class GoogleHealthApiException : Exception
{
    public int StatusCode { get; }

    /// <summary>
    /// True when the API rejected the request payload itself — a 400 carrying a
    /// `google.rpc.BadRequest` with field violations. That is always a bug on this side, so callers
    /// that tolerate a 400 as "this account has no such data" must not swallow one of these.
    /// </summary>
    public bool IsMalformedRequest { get; }

    public GoogleHealthApiException(int statusCode, string message, bool isMalformedRequest = false)
        : base(message)
    {
        StatusCode = statusCode;
        IsMalformedRequest = isMalformedRequest;
    }
}
