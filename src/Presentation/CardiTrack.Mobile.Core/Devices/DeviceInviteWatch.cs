namespace CardiTrack.Mobile.Core.Devices;

/// <summary>What the caregiver's waiting screen should be showing.</summary>
public enum DeviceInviteWatchState
{
    /// <summary>Handed over; nobody has opened it yet.</summary>
    Waiting = 1,

    /// <summary>The wearer has opened the link. Still waiting, but they are looking at it.</summary>
    Opened = 2,

    /// <summary>They granted access and a device is connected. Terminal.</summary>
    Connected = 3,

    /// <summary>They said it was not them. Terminal.</summary>
    Declined = 4,

    /// <summary>The deadline passed with nobody finishing. Terminal, and offers another go.</summary>
    Expired = 5,

    /// <summary>The caregiver cancelled, or replaced it with a newer invitation. Terminal.</summary>
    Cancelled = 6,
}

/// <summary>
/// The caregiver's side of an invitation while the wearer decides: what to show, and when to ask
/// the server again.
/// </summary>
/// <remarks>
/// <para>
/// Lives in Mobile.Core, away from MAUI, because everything interesting about this screen is a
/// decision rather than a layout — which state a status string means, when a deadline has passed,
/// whether to keep polling — and those are worth testing without a device in the loop.
/// </para>
/// <para>
/// <strong>It never decides that an invitation has expired on its own initiative before the server
/// has.</strong> The server reports <c>expired</c> as a status, and this trusts that over its own
/// arithmetic wherever the two could disagree. The local clock is only consulted for the countdown
/// the screen renders, and for the case the server cannot answer at all.
/// </para>
/// </remarks>
public sealed class DeviceInviteWatch
{
    /// <summary>
    /// How often to ask while the wearer has not opened the link yet. Slow enough not to be a
    /// battery cost on a screen somebody may leave open, fast enough that the caregiver sees
    /// something happen shortly after it does.
    /// </summary>
    public static readonly TimeSpan IdlePollInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How often to ask once they have opened it. Quicker, because from here the next change is the
    /// one the caregiver is waiting for and it can arrive at any moment.
    /// </summary>
    public static readonly TimeSpan ActivePollInterval = TimeSpan.FromSeconds(3);

    private readonly TimeProvider _time;

    public DeviceInviteWatch(DateTime expiresAtUtc, TimeProvider? time = null)
    {
        ExpiresAtUtc = expiresAtUtc;
        _time = time ?? TimeProvider.System;
        State = DeviceInviteWatchState.Waiting;
    }

    public DateTime ExpiresAtUtc { get; }

    public DeviceInviteWatchState State { get; private set; }

    /// <summary>The connection the grant produced, once <see cref="DeviceInviteWatchState.Connected"/>.</summary>
    public Guid? DeviceId { get; private set; }

    /// <summary>Whether this is over, however it ended.</summary>
    public bool IsFinished => State
        is DeviceInviteWatchState.Connected
        or DeviceInviteWatchState.Declined
        or DeviceInviteWatchState.Expired
        or DeviceInviteWatchState.Cancelled;

    /// <summary>How long is left, floored at zero so a passed deadline never renders as negative.</summary>
    public TimeSpan Remaining
    {
        get
        {
            var left = ExpiresAtUtc - _time.GetUtcNow().UtcDateTime;
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }
    }

    /// <summary>How long to wait before asking again, or null once there is nothing left to wait for.</summary>
    public TimeSpan? NextPollDelay => State switch
    {
        DeviceInviteWatchState.Waiting => IdlePollInterval,
        DeviceInviteWatchState.Opened => ActivePollInterval,
        _ => null,
    };

    /// <summary>
    /// Folds in what the server just said. Returns whether the state changed, so a caller can
    /// re-render only when there is something new to show.
    /// </summary>
    /// <param name="status">
    /// The API's own word: pending, opened, completed, declined, revoked or expired. Anything
    /// unrecognised leaves the state alone — a status this build has never heard of is a newer
    /// server, not a reason to tell the caregiver their invitation failed.
    /// </param>
    /// <param name="deviceId">The connection id, when the status is <c>completed</c>.</param>
    public bool Apply(string? status, Guid? deviceId = null)
    {
        var next = status?.ToLowerInvariant() switch
        {
            "pending" => DeviceInviteWatchState.Waiting,
            "opened" => DeviceInviteWatchState.Opened,
            "completed" => DeviceInviteWatchState.Connected,
            "declined" => DeviceInviteWatchState.Declined,
            "revoked" => DeviceInviteWatchState.Cancelled,
            "expired" => DeviceInviteWatchState.Expired,
            _ => State,
        };

        // Terminal states are terminal. A poll already in flight when the wearer finished can land
        // afterwards carrying the older answer, and letting it move a connected invitation back to
        // "waiting" would flicker the screen back to a QR code nobody should scan again.
        if (IsFinished && next != State)
            return false;

        var changed = next != State || (deviceId is not null && deviceId != DeviceId);
        State = next;

        if (next == DeviceInviteWatchState.Connected && deviceId is not null)
            DeviceId = deviceId;

        return changed;
    }

    /// <summary>
    /// Marks it expired because the deadline has passed and we are not going to hear otherwise.
    /// Returns whether that changed anything.
    /// </summary>
    /// <remarks>
    /// For the case the server is unreachable — the caregiver's phone has lost signal, say. The
    /// server's own <c>expired</c> is preferred wherever it can be had; this is the fallback that
    /// stops a waiting screen spinning forever against a deadline that has demonstrably gone.
    /// </remarks>
    public bool ExpireIfElapsed()
    {
        if (IsFinished || Remaining > TimeSpan.Zero)
            return false;

        State = DeviceInviteWatchState.Expired;
        return true;
    }

    /// <summary>The caregiver cancelled. Terminal, and beats anything a later poll says.</summary>
    public bool Cancel()
    {
        if (IsFinished)
            return false;

        State = DeviceInviteWatchState.Cancelled;
        return true;
    }
}
