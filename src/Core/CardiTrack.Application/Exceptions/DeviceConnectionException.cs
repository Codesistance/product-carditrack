namespace CardiTrack.Application.Exceptions;

/// <summary>
/// Raised by the device-connection flow with a machine-readable code the API maps to a
/// status per docs/execution/backend/api/devices.md.
/// </summary>
public class DeviceConnectionException : Exception
{
    public const string UnsupportedProvider = "UNSUPPORTED_PROVIDER";
    public const string InvalidStateToken = "INVALID_STATE_TOKEN";
    public const string OAuthExchangeFailed = "OAUTH_EXCHANGE_FAILED";

    public string Code { get; }

    public DeviceConnectionException(string code, string message, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
    }
}
