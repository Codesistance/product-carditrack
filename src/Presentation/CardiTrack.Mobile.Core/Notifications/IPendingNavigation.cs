namespace CardiTrack.Mobile.Core.Notifications;

/// <summary>
/// A deep link that arrived before the shell was ready to consume it. Sign-out must discard
/// it, or the next caregiver's AppShell will navigate to the previous account's destination.
/// </summary>
public interface IPendingNavigation
{
    void Discard();
}
