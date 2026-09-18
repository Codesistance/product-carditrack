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

    /// <summary>
    /// The invitation a request names is not live — unknown, expired, already used, declined or
    /// revoked. Deliberately one code covering all five: the caregiver's app renders the distinction
    /// from the invite's own status, which it is entitled to read, and the wearer-facing page must
    /// not be able to tell them apart at all.
    /// </summary>
    public const string InviteNotLive = "INVITE_NOT_LIVE";

    /// <summary>The channel on a create-invite request is neither <c>link</c> nor <c>qr</c>.</summary>
    public const string UnsupportedInviteChannel = "UNSUPPORTED_INVITE_CHANNEL";

    public string Code { get; }

    public DeviceConnectionException(string code, string message, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
    }
}
