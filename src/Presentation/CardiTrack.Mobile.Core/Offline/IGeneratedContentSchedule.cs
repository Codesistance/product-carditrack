namespace CardiTrack.Mobile.Core.Offline;

/// <summary>Which pipeline-written card a schedule entry is about.</summary>
public enum GeneratedCard
{
    /// <summary>The family summary.</summary>
    Digest,

    /// <summary>The wellness suggestion — "Something to try".</summary>
    Advise,

    /// <summary>The questions waiting to be answered.</summary>
    Questions,
}

/// <summary>
/// Remembers when each CardiMember's generated cards were last read, so the cadence in
/// <see cref="GeneratedContentRefresh"/> survives the screen that reads them.
/// </summary>
/// <remarks>
/// <para>
/// It has to live outside the page. CardiMemberDetailPage is registered
/// <c>AddTransient</c> behind a plain <c>Routing.RegisterRoute</c>, so Shell builds a fresh one
/// on every navigation and any timestamp kept in a field resets with it. A caregiver stepping
/// back to the dashboard and returning looked exactly like a first visit, and re-read all four
/// payloads every time — the cadence only ever applied to time spent standing still on the page.
/// </para>
/// <para>
/// Keyed by member as well as card, so one CardiMember's read says nothing about another's. That
/// is also what makes it safe for a page that is handed a different member than the one it last
/// loaded: there is no single "last read" to reset at the wrong moment.
/// </para>
/// </remarks>
public interface IGeneratedContentSchedule
{
    /// <summary>
    /// Whether <paramref name="card"/> should be read for this member on this pass.
    /// </summary>
    /// <param name="requestedByCaregiver">
    /// True only for a refresh the caregiver asked for by hand — a pull, or the retry button.
    /// Arriving on the screen is not one: navigation is how you get to a page, not a way of
    /// asking for its contents again, and treating it as a request is what let a caregiver
    /// stepping in and out re-read everything each time.
    /// </param>
    bool IsDue(Guid cardiMemberId, GeneratedCard card, bool requestedByCaregiver);

    /// <summary>
    /// Records that this card was read for this member, now. Called for a read that was made for
    /// a member the screen ends up showing — not for one that was launched and abandoned.
    /// </summary>
    void Record(Guid cardiMemberId, GeneratedCard card);
}
