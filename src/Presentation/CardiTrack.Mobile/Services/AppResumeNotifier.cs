namespace CardiTrack.Mobile.Services;

/// <summary>
/// The app coming back to the foreground, as one signal any page can subscribe to.
/// </summary>
/// <remarks>
/// MAUI raises this on the <see cref="Microsoft.Maui.Controls.Window"/>, not on the page, and
/// <c>OnAppearing</c> is no substitute: it is a navigation event, and whether a backgrounded app
/// raises it again on the way back is platform-specific (Android does, iOS does not). Routing the
/// window event through here gives every screen the same definition of "the caregiver just came
/// back to the app".
/// </remarks>
public interface IAppResumeNotifier
{
    event EventHandler Resumed;
}

/// <inheritdoc />
internal sealed class AppResumeNotifier : IAppResumeNotifier
{
    public event EventHandler? Resumed;

    /// <summary>Called by <see cref="App"/> from the window's own Resumed event.</summary>
    public void NotifyResumed() => Resumed?.Invoke(this, EventArgs.Empty);
}
