using CardiTrack.Mobile.Core.Offline;

namespace CardiTrack.UnitTests.Mobile;

/// <summary>
/// The rule the member detail page's summary, suggestion and question loads run under: they are
/// started but not waited for, two passes can have reads out for the same card at once, and the
/// older answer must not be the one that stays on screen (#1105).
/// </summary>
public class FollowUpGateTests
{
    private static readonly Guid Member = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Another = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void A_pass_draws_its_live_answer()
    {
        var gate = new FollowUpGate();

        Assert.True(gate.MayDrawLive(gate.Begin(), Member, GeneratedCard.Digest));
    }

    /// <summary>The bug itself: two pulls inside one slow round trip, older answer last.</summary>
    [Fact]
    public void An_older_pass_does_not_draw_over_a_newer_answer()
    {
        var gate = new FollowUpGate();
        var first = gate.Begin();
        var second = gate.Begin();

        Assert.True(gate.MayDrawLive(second, Member, GeneratedCard.Digest));
        Assert.False(gate.MayDrawLive(first, Member, GeneratedCard.Digest));
    }

    /// <summary>
    /// Why the order is kept per card rather than per pass. The later pass usually skips the round
    /// trip the cadence has already paid for, so "something newer exists" would drop an answer
    /// that nothing is coming to replace and leave the caregiver on saved text for the whole
    /// window.
    /// </summary>
    [Fact]
    public void An_older_pass_still_draws_a_card_no_newer_answer_has_reached()
    {
        var gate = new FollowUpGate();
        var first = gate.Begin();
        gate.Begin();

        Assert.True(gate.MayDrawLive(first, Member, GeneratedCard.Digest));
    }

    /// <summary>
    /// One card's answer says nothing about another's: the three loads are separate round trips
    /// and any one of them can be the slow one.
    /// </summary>
    [Fact]
    public void One_card_being_drawn_does_not_close_another()
    {
        var gate = new FollowUpGate();
        var first = gate.Begin();
        var second = gate.Begin();

        Assert.True(gate.MayDrawLive(second, Member, GeneratedCard.Digest));

        Assert.True(gate.MayDrawLive(first, Member, GeneratedCard.Advise));
        Assert.True(gate.MayDrawLive(first, Member, GeneratedCard.Questions));
    }

    /// <summary>
    /// A pass superseded on one card stays superseded on it, and the winner is not locked out by
    /// the loser's attempt.
    /// </summary>
    [Fact]
    public void A_superseded_pass_stays_superseded()
    {
        var gate = new FollowUpGate();
        var first = gate.Begin();
        var second = gate.Begin();

        Assert.True(gate.MayDrawLive(second, Member, GeneratedCard.Advise));
        Assert.False(gate.MayDrawLive(first, Member, GeneratedCard.Advise));
        Assert.False(gate.MayDrawLive(first, Member, GeneratedCard.Advise));
        Assert.True(gate.MayDrawLive(second, Member, GeneratedCard.Advise));
    }

    /// <summary>
    /// The ordinary shape of these loads: the device's saved copy goes up first so the card reads
    /// as written, and the live answer lands on top of it.
    /// </summary>
    [Fact]
    public void The_saved_copy_goes_up_while_the_answer_is_still_out()
    {
        var gate = new FollowUpGate();
        var pass = gate.Begin();

        Assert.True(gate.MayDrawSaved(Member, GeneratedCard.Digest));
        Assert.True(gate.MayDrawLive(pass, Member, GeneratedCard.Digest));
    }

    /// <summary>
    /// A peek is a wait of its own, so an answer can arrive while it is out. The cache must not go
    /// back over fresher words — whichever pass read it.
    /// </summary>
    [Fact]
    public void The_saved_copy_stands_aside_once_an_answer_has_drawn()
    {
        var gate = new FollowUpGate();
        gate.MayDrawLive(gate.Begin(), Member, GeneratedCard.Digest);

        Assert.False(gate.MayDrawSaved(Member, GeneratedCard.Digest));
        Assert.True(gate.MayDrawSaved(Member, GeneratedCard.Advise));
    }

    /// <summary>
    /// Reading the saved copy takes no place in the order: an answer already in flight from an
    /// earlier pass is still the newer thing, and still draws when it lands.
    /// </summary>
    [Fact]
    public void The_saved_copy_does_not_supersede_an_answer_still_in_flight()
    {
        var gate = new FollowUpGate();
        var first = gate.Begin();
        gate.Begin();

        Assert.True(gate.MayDrawSaved(Member, GeneratedCard.Digest));
        Assert.True(gate.MayDrawLive(first, Member, GeneratedCard.Digest));
    }

    /// <summary>
    /// Shell hands a page a different CardiMember without rebuilding it. Nothing has drawn the new
    /// one's cards, so their saved copies go up as they would on a fresh page — and the pass that
    /// drew the last member's summary does not hold the new member's back.
    /// </summary>
    [Fact]
    public void Another_members_cards_have_been_drawn_by_nobody()
    {
        var gate = new FollowUpGate();
        var first = gate.Begin();
        gate.MayDrawLive(first, Member, GeneratedCard.Digest);

        var second = gate.Begin();

        Assert.True(gate.MayDrawSaved(Another, GeneratedCard.Digest));
        Assert.True(gate.MayDrawLive(second, Another, GeneratedCard.Digest));
        Assert.False(gate.MayDrawSaved(Member, GeneratedCard.Digest));
    }

    /// <summary>
    /// The same page handed A, then B, then A again, inside the window the cadence has already
    /// paid for. The pass makes no round trip, so the saved copy is the whole card — and a draw
    /// from the presentation the caregiver has already left must not be what refuses it.
    /// </summary>
    [Fact]
    public void A_cleared_screen_may_draw_a_member_it_has_drawn_before()
    {
        var gate = new FollowUpGate();
        gate.MayDrawLive(gate.Begin(), Member, GeneratedCard.Digest);

        gate.Cleared();

        Assert.True(gate.MayDrawSaved(Member, GeneratedCard.Digest));
    }

    /// <summary>
    /// Clearing is not a way back in for an answer that has already been superseded: the pass
    /// counter keeps running, so a read still in flight from before the change is refused once the
    /// new presentation has drawn.
    /// </summary>
    [Fact]
    public void Clearing_does_not_let_an_older_pass_draw_over_a_newer_one()
    {
        var gate = new FollowUpGate();
        var before = gate.Begin();

        gate.Cleared();

        var after = gate.Begin();
        Assert.True(gate.MayDrawLive(after, Member, GeneratedCard.Digest));
        Assert.False(gate.MayDrawLive(before, Member, GeneratedCard.Digest));
    }

    /// <summary>
    /// And an answer that is still the newest thing there is still draws after a clear — the
    /// screen took its cards down, it did not decide the read was worthless.
    /// </summary>
    [Fact]
    public void Clearing_does_not_throw_away_an_answer_nothing_has_superseded()
    {
        var gate = new FollowUpGate();
        var pass = gate.Begin();

        gate.Cleared();

        Assert.True(gate.MayDrawLive(pass, Member, GeneratedCard.Digest));
    }
}
