namespace CardiTrack.Mobile.Core.Offline;

/// <summary>
/// Orders a screen's follow-up loads — the reads that run beside the main one and draw when they
/// land rather than being waited for. Each pass takes a <see cref="FollowUpPass"/>, and a live
/// answer draws its card only while nothing newer has drawn it.
/// </summary>
/// <remarks>
/// <para>
/// Separate from <see cref="LoadGate"/>, deliberately. The gate is for the load a screen waits
/// for: it carries a token, and <see cref="LoadGate.Release"/> disposes that token's source as
/// soon as the main load settles. These loads outlive exactly that moment — the member is on
/// screen and the gate long released while the summary is still out — so they cannot ride on the
/// gate's ticket. What they need is not a token but an order: a plain counter with no lifetime to
/// get wrong (#1105).
/// </para>
/// <para>
/// Kept per card rather than per pass, which is the whole of the difference. "Is this the newest
/// pass?" would be simpler and would be wrong: a later pass usually skips the round trip the
/// cadence has already paid for (<see cref="GeneratedContentSchedule"/>), so letting it supersede
/// would throw away an answer nothing is coming to replace, and leave the caregiver on saved text
/// until the next window opened. The narrower question — has anything newer already drawn this
/// card — is the only one whose answer is worth dropping a result over.
/// </para>
/// <para>
/// Keyed by member as well, for the same reason the schedule is: a page can be handed a different
/// CardiMember without being rebuilt, and that member's cards have been drawn by nobody. Keyed by
/// card alone, the new member's saved copy would be refused on the strength of the last member's
/// answer, and their card would sit on its placeholder until a live read happened to be due.
/// </para>
/// <para>
/// Nothing here cancels anything, also deliberately. A superseded read is still worth finishing:
/// its body fills the device's cache, which is what the peek on the next pass reads, and the
/// loaders await it anyway so that a failure is observed rather than left on a dropped task.
/// </para>
/// <para>
/// Not thread-safe, like the gate beside it — every caller is a page's UI thread.
/// </para>
/// </remarks>
public sealed class FollowUpGate
{
    /// <summary>
    /// The newest pass to have drawn each of a member's cards from the network, absent until one
    /// has. Bounded by the CardiMembers one page instance is handed, times the three cards.
    /// </summary>
    private readonly Dictionary<(Guid Member, GeneratedCard Card), int> _drawnLiveBy = [];

    private int _pass;

    /// <summary>
    /// Opens a pass. Never refuses: ordering these loads is not a way of skipping them.
    /// </summary>
    public FollowUpPass Begin() => new(++_pass);

    /// <summary>
    /// Whether <paramref name="pass"/> may draw its live answer for this member's
    /// <paramref name="card"/> now — recording it as the newest answer that card has, when it may.
    /// </summary>
    /// <remarks>
    /// Asked and answered in one call because the two halves cannot usefully be separated: between
    /// a check and a later "I drew it" sits the drawing itself, and a caller that forgot the second
    /// call would quietly be no better off than with no gate at all.
    /// A pass never locks itself out, so a load free to redraw its own card — a retry, or a second
    /// answer for the same one — still goes up.
    /// </remarks>
    public bool MayDrawLive(FollowUpPass pass, Guid cardiMemberId, GeneratedCard card)
    {
        var key = (cardiMemberId, card);
        if (_drawnLiveBy.TryGetValue(key, out var drawnBy) && pass.Generation < drawnBy)
            return false;

        _drawnLiveBy[key] = pass.Generation;
        return true;
    }

    /// <summary>
    /// Whether the device's saved copy of this member's <paramref name="card"/> may still go up:
    /// only while no live answer has drawn it.
    /// </summary>
    /// <remarks>
    /// Not a question about passes, and it records nothing. The saved copy is the same bytes
    /// whichever pass reads it, and it is older than every live answer by construction, so it
    /// stands aside for all of them rather than taking a place in the order. What it must not do
    /// is land on a card a live answer has already drawn — that happens when a peek is still out
    /// while another pass's answer arrives, and it would put the cache back over fresher words.
    /// A live answer still in flight has drawn nothing yet, which is the ordinary case this is
    /// written for: the saved copy goes up at once so the card reads as written, and the answer
    /// lands on top of it when it comes.
    /// </remarks>
    public bool MayDrawSaved(Guid cardiMemberId, GeneratedCard card) =>
        !_drawnLiveBy.ContainsKey((cardiMemberId, card));
}

/// <summary>The identity of one round of follow-up loads: its place in the sequence.</summary>
public readonly record struct FollowUpPass(int Generation);
