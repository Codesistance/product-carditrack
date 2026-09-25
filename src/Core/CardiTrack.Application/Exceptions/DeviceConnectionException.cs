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

    /// <summary>
    /// A reconnect came back signed in to a different provider account from the one the
    /// connection holds. Refused rather than switched in place: that would quietly swap whose
    /// health data this card shows. The client offers "Change device" instead.
    /// </summary>
    public const string DifferentAccount = "DIFFERENT_ACCOUNT";

    /// <summary>
    /// A replacement came back signed in to an account another of the member's connections
    /// already holds, so completing it would leave two cards reading one data stream.
    /// </summary>
    public const string AccountAlreadyConnected = "ACCOUNT_ALREADY_CONNECTED";

    /// <summary>
    /// Suspending this connection would leave the member with no device collecting. Pausing
    /// monitoring is the bounded way to do that, so the caregiver is pointed there instead.
    /// </summary>
    public const string LastActiveDevice = "LAST_ACTIVE_DEVICE";

    /// <summary>The action needs a collecting connection and this one is suspended.</summary>
    public const string DeviceSuspended = "DEVICE_SUSPENDED";

    /// <summary>A reconnect named a brand other than the connection's own.</summary>
    public const string ProviderMismatch = "PROVIDER_MISMATCH";

    /// <summary>
    /// Whether this failure is a conflict with the member's current devices (409) rather than a
    /// malformed request (400) or a provider failure (502).
    /// </summary>
    public bool IsConflict => Code is DifferentAccount or AccountAlreadyConnected or LastActiveDevice or DeviceSuspended;

    public string Code { get; }

    public DeviceConnectionException(string code, string message, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
    }
}
