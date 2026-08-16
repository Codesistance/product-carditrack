namespace CardiTrack.Mobile.Core.Navigation;

/// <summary>
/// Two back-swipes to leave the app from the dashboard tab root. The first press arms a
/// short window and must not exit; a second press inside that window confirms.
/// </summary>
/// <remarks>
/// Android finishes the activity on a back press at a tab root. From the dashboard that is
/// indistinguishable from a crash — especially right after onboarding lands here. Other tab
/// roots keep Android's own meaning; only this screen, the one the rest of the app treats as
/// home, asks twice.
/// </remarks>
public sealed class ExitConfirmation
{
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(2);

    private readonly TimeProvider _clock;
    private DateTimeOffset? _armedAt;

    public ExitConfirmation(TimeProvider? clock = null) =>
        _clock = clock ?? TimeProvider.System;

    /// <summary>Whether a second press right now would confirm the exit.</summary>
    public bool IsArmed =>
        _armedAt is { } armed && _clock.GetUtcNow() - armed <= Window;

    /// <summary>
    /// First call arms and returns <c>false</c> (do not exit). A second call within
    /// <see cref="Window"/> returns <c>true</c> (exit) and disarms. A late second call is
    /// treated as a new first press.
    /// </summary>
    public bool Confirm()
    {
        var now = _clock.GetUtcNow();
        if (_armedAt is { } armed && now - armed <= Window)
        {
            _armedAt = null;
            return true;
        }

        _armedAt = now;
        return false;
    }

    public void Disarm() => _armedAt = null;
}
