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
    /// Whether <paramref name="card"/> should be read for this member on this pass — and, when it
    /// should, the pass's claim on that read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Asking and claiming are one call because apart they leave a gap. A pass records its read
    /// only once its member load has settled, and the page is transient: leaving and coming back
    /// inside that second builds a second page, which sees no timestamp yet, finds the same cards
    /// due, and reads all three again while the first page's reads are still out. The claim closes
    /// that window — the second page is told the read is already happening, and its peek picks the
    /// answer up from the cache instead.
    /// </para>
    /// <para>
    /// A claim is not a queue. The passed-over read does not happen later; the card simply stays
    /// as the caregiver last saw it until the pass that holds the claim lands, or until the
    /// screen's next tick finds it due again. That is the same trade the peek already makes for a
    /// read another pass has in flight, and it is why the claim is worth having: the cost is a
    /// card that waits, and the saving is three requests that would have asked for what was
    /// already on its way.
    /// </para>
    /// <para>
    /// The claim expires on its own — see <see cref="GeneratedContentRefresh.ClaimLease"/> — so a
    /// pass that never comes back cannot hold a card shut.
    /// </para>
    /// </remarks>
    /// <param name="requestedByCaregiver">
    /// True only for a refresh the caregiver asked for by hand — a pull, or the retry button.
    /// Arriving on the screen is not one: navigation is how you get to a page, not a way of
    /// asking for its contents again, and treating it as a request is what let a caregiver
    /// stepping in and out re-read everything each time.
    /// A request is never held back by somebody else's claim either. Someone who pulls the screen
    /// down is entitled to an answer of their own rather than a promise that one is coming, and
    /// two reads landing out of order is the render gate's problem, not the schedule's.
    /// </param>
    ReadClaim ClaimIfDue(Guid cardiMemberId, GeneratedCard card, bool requestedByCaregiver);

    /// <summary>
    /// Records that this card was read for this member, now, and gives the claim back. Called for
    /// a read that was made for a member the screen ends up showing — not for one that was
    /// launched and abandoned. A claim that was never held records nothing, so a caller can hand
    /// back every card it asked about without sorting them first.
    /// </summary>
    /// <param name="claim">
    /// What <see cref="ClaimIfDue"/> handed out. It carries the session the read started in: a
    /// read that outlived its session is dropped rather than written, because a load is not
    /// cancelled by sign-out, so one belonging to the caregiver who has just signed out can
    /// otherwise land in the next caregiver's schedule and hold back a member they have never
    /// opened. Same guard, and the same reason, as the generation the API client captures before
    /// a GET and checks before caching its body.
    /// </param>
    void Record(Guid cardiMemberId, GeneratedCard card, ReadClaim claim);

    /// <summary>
    /// Gives a claim back without recording a read: the pass that took it never put this member
    /// on the screen, so nothing was read that anybody saw.
    /// </summary>
    /// <remarks>
    /// The card goes back to whatever the clock says about it, which is usually "due" — the next
    /// pass reads it. Holding it instead would make an abandoned pass quieter than a completed
    /// one, which is the wrong way round.
    /// </remarks>
    void Abandon(Guid cardiMemberId, GeneratedCard card, ReadClaim claim);
}

/// <summary>
/// One pass's hold on one card's read: proof that this pass is the one reading it, and the
/// session the read started in.
/// </summary>
/// <remarks>
/// Carried rather than looked up again, because both halves are about the moment the read began.
/// The ticket says which pass holds the card — a caregiver's own request can take a claim over,
/// and the pass it was taken from must not then hand back a hold it no longer has — and the
/// session says whose schedule the answer belongs in.
/// </remarks>
public readonly record struct ReadClaim(int Session, int Ticket)
{
    /// <summary>
    /// The answer for a card this pass is not reading: nothing to record, nothing to give back.
    /// </summary>
    public static ReadClaim None => default;

    /// <summary>
    /// True when this pass holds the read — the card was due, and nobody else's read was already
    /// out for it.
    /// </summary>
    public bool IsDue => Ticket != 0;
}
