namespace CardiTrack.Application.Interfaces.Services;

/// <summary>
/// Asks, on behalf of an arriving caregiver, that the medical model be resident by the time they
/// have something to say to it.
/// </summary>
/// <remarks>
/// A request, not a command: the implementation decides whether one is worth issuing, runs it
/// detached from whatever asked, and never reports back. Nothing downstream waits on a warm-up —
/// if it does not happen, or fails, the next real call simply pays the load it always paid.
/// </remarks>
public interface IAiWarmUpService
{
    /// <summary>
    /// Returns immediately, having at most started a warm-up. Never throws: a caller is a page
    /// load or a sign-in, and neither may fail because a nicety did.
    /// </summary>
    void RequestWarmUp();
}
