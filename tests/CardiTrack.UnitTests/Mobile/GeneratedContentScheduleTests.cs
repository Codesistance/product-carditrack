using CardiTrack.Mobile.Core.Auth;
using CardiTrack.Mobile.Core.Offline;

namespace CardiTrack.UnitTests.Mobile;

public class GeneratedContentScheduleTests
{
    private static readonly Guid Dad = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Mum = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private DateTime _now = new(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc);

    private GeneratedContentSchedule Schedule(SessionGeneration? session = null) =>
        new(session ?? new SessionGeneration(), () => _now);

    private void Advance(TimeSpan by) => _now += by;

    /// <summary>
    /// A pass that claimed the read and got the member on screen — the ordinary way a stamp is
    /// written. Claims as a caregiver's request so the setup is never refused by another claim.
    /// </summary>
    private static void ReadAndRecord(
        GeneratedContentSchedule schedule, Guid member, GeneratedCard card) =>
        schedule.Record(member, card, schedule.ClaimIfDue(member, card, requestedByCaregiver: true));

    /// <summary>
    /// Whether an unattended pass would read this card. Takes the claim when the answer is yes,
    /// exactly as the page's own pass would — which is the point of asking through the claim.
    /// </summary>
    private static bool DueUnattended(
        GeneratedContentSchedule schedule, Guid member, GeneratedCard card) =>
        schedule.ClaimIfDue(member, card, requestedByCaregiver: false).IsDue;

    [Fact]
    public void A_card_never_read_is_due()
    {
        var schedule = Schedule();

        Assert.True(DueUnattended(schedule, Dad, GeneratedCard.Digest));
    }

    /// <summary>
    /// The whole point of this type. The page is rebuilt on every navigation, so a caregiver
    /// stepping back to the dashboard and returning used to look like a first visit and re-read
    /// everything; the schedule outlives the page, so it does not.
    /// </summary>
    [Fact]
    public void A_read_survives_the_page_that_made_it()
    {
        var schedule = Schedule();
        ReadAndRecord(schedule, Dad, GeneratedCard.Digest);

        Advance(TimeSpan.FromSeconds(30));

        Assert.False(DueUnattended(schedule, Dad, GeneratedCard.Digest));
    }

    [Fact]
    public void A_read_stops_holding_once_the_interval_has_passed()
    {
        var schedule = Schedule();
        ReadAndRecord(schedule, Dad, GeneratedCard.Digest);

        Advance(GeneratedContentRefresh.Interval);

        Assert.True(DueUnattended(schedule, Dad, GeneratedCard.Digest));
    }

    /// <summary>
    /// A pull or the retry button. Someone who asks is entitled to be told that nothing moved.
    /// </summary>
    [Fact]
    public void A_caregiver_who_asks_is_never_held_back()
    {
        var schedule = Schedule();
        ReadAndRecord(schedule, Dad, GeneratedCard.Digest);

        Assert.True(schedule.ClaimIfDue(Dad, GeneratedCard.Digest, requestedByCaregiver: true).IsDue);
    }

    [Fact]
    public void One_member_s_read_says_nothing_about_another_s()
    {
        var schedule = Schedule();
        ReadAndRecord(schedule, Dad, GeneratedCard.Digest);

        Assert.True(DueUnattended(schedule, Mum, GeneratedCard.Digest));
    }

    /// <summary>
    /// The question is skipped on its own when an editor is open, so recording the summary must
    /// not stand in for it — otherwise closing the editor would leave a question another
    /// caregiver had already answered on screen for the rest of the window.
    /// </summary>
    [Fact]
    public void One_card_s_read_says_nothing_about_another_card()
    {
        var schedule = Schedule();
        ReadAndRecord(schedule, Dad, GeneratedCard.Digest);
        ReadAndRecord(schedule, Dad, GeneratedCard.Advise);

        Assert.True(DueUnattended(schedule, Dad, GeneratedCard.Questions));
    }

    // Not tested here: that a pass which never put the member on screen records nothing. Whether
    // Record is called is CardiMemberDetailPage's decision, and asserting it from this side only
    // restates "never read is due", which the first test already covers. A version of that test
    // was written and removed for claiming more than it checked.

    /// <summary>
    /// A shared phone. The next caregiver must not inherit the last one's timings, and their
    /// member ids must not still be sitting in the dictionary.
    /// </summary>
    [Fact]
    public void Signing_out_drops_everything()
    {
        var session = new SessionGeneration();
        var schedule = Schedule(session);
        ReadAndRecord(schedule, Dad, GeneratedCard.Digest);
        Assert.False(DueUnattended(schedule, Dad, GeneratedCard.Digest));

        session.Advance();

        Assert.True(DueUnattended(schedule, Dad, GeneratedCard.Digest));
    }

    /// <summary>
    /// The read that outlived its caregiver. A detail-page load is not cancelled by sign-out, so
    /// one started by the caregiver signing out can land after the next one has signed in — by
    /// which time the next caregiver has usually looked at something, so the schedule is already
    /// current and has nothing to drop. Only the session the read started in can say it is stale.
    /// Left unguarded, a member the new caregiver has never opened would be held back on their
    /// first visit, showing the placeholder over a summary they cannot see.
    /// </summary>
    [Fact]
    public void A_read_that_outlived_its_session_is_not_written()
    {
        var session = new SessionGeneration();
        var schedule = Schedule(session);
        var startedIn = schedule.ClaimIfDue(Dad, GeneratedCard.Digest, requestedByCaregiver: false);

        session.Advance();                 // sign-out
        session.Advance();                 // the next caregiver signs in
        _ = DueUnattended(schedule, Mum, GeneratedCard.Digest);

        schedule.Record(Dad, GeneratedCard.Digest, startedIn);

        Assert.True(DueUnattended(schedule, Dad, GeneratedCard.Digest));
    }

    [Fact]
    public void A_session_that_has_not_moved_keeps_what_it_recorded()
    {
        var session = new SessionGeneration();
        var schedule = Schedule(session);
        ReadAndRecord(schedule, Dad, GeneratedCard.Digest);

        Assert.False(DueUnattended(schedule, Dad, GeneratedCard.Digest));
        Assert.False(DueUnattended(schedule, Dad, GeneratedCard.Digest));
    }

    // The claim: what covers the gap between a read being started and being written down.

    /// <summary>
    /// The second page. A caregiver who leaves and comes back inside the member fetch gets a new
    /// page instance, which finds no stamp — because the first page writes one only once its own
    /// member load has settled — and would read all three cards again beside the reads already
    /// out. It is told they are already being read.
    /// </summary>
    [Fact]
    public void A_read_already_out_is_not_started_again()
    {
        var schedule = Schedule();

        Assert.True(DueUnattended(schedule, Dad, GeneratedCard.Digest));
        Assert.False(DueUnattended(schedule, Dad, GeneratedCard.Digest));
    }

    /// <summary>
    /// One card at a time, for one member at a time: a read of the summary says nothing about the
    /// suggestion, or about anybody else's summary.
    /// </summary>
    [Fact]
    public void A_read_out_for_one_card_does_not_hold_another()
    {
        var schedule = Schedule();
        Assert.True(DueUnattended(schedule, Dad, GeneratedCard.Digest));

        Assert.True(DueUnattended(schedule, Dad, GeneratedCard.Advise));
        Assert.True(DueUnattended(schedule, Mum, GeneratedCard.Digest));
    }

    /// <summary>
    /// A pull is not a request to be told that somebody else has already asked. The two answers
    /// landing out of order is the page's render gate to deal with, not a reason to refuse the
    /// gesture.
    /// </summary>
    [Fact]
    public void A_caregiver_who_asks_is_not_held_by_someone_else_s_read()
    {
        var schedule = Schedule();
        Assert.True(DueUnattended(schedule, Dad, GeneratedCard.Digest));

        Assert.True(schedule.ClaimIfDue(Dad, GeneratedCard.Digest, requestedByCaregiver: true).IsDue);
    }

    /// <summary>
    /// A pass that never put the member on screen read nothing anybody saw, so it leaves no stamp
    /// — and must not leave its claim behind either, or the pass that does show the member would
    /// be turned away by a read that is over.
    /// </summary>
    [Fact]
    public void A_claim_handed_back_unread_leaves_the_card_due()
    {
        var schedule = Schedule();
        var claim = schedule.ClaimIfDue(Dad, GeneratedCard.Digest, requestedByCaregiver: false);

        schedule.Abandon(Dad, GeneratedCard.Digest, claim);

        Assert.True(DueUnattended(schedule, Dad, GeneratedCard.Digest));
    }

    /// <summary>
    /// Recording hands the claim back too: from then on it is the stamp that holds the card, and
    /// the stamp is what expires on the cadence.
    /// </summary>
    [Fact]
    public void A_recorded_read_is_held_by_its_stamp_rather_than_its_claim()
    {
        var schedule = Schedule();
        ReadAndRecord(schedule, Dad, GeneratedCard.Digest);

        Advance(GeneratedContentRefresh.Interval);

        Assert.True(DueUnattended(schedule, Dad, GeneratedCard.Digest));
    }

    /// <summary>
    /// The failure a claim must not be able to cause. A pass whose request hangs, or that faults
    /// before it hands anything back, would otherwise hold its card shut for the life of the app —
    /// which is worse than the duplicate read the claim exists to prevent.
    /// </summary>
    [Fact]
    public void A_claim_nobody_gives_back_expires()
    {
        var schedule = Schedule();
        Assert.True(DueUnattended(schedule, Dad, GeneratedCard.Digest));

        Advance(GeneratedContentRefresh.ClaimLease);

        Assert.True(DueUnattended(schedule, Dad, GeneratedCard.Digest));
    }

    /// <summary>
    /// And it expires well before the cadence window it sits inside, so a lost claim costs part of
    /// one window rather than a whole one.
    /// </summary>
    [Fact]
    public void The_lease_is_shorter_than_the_window_it_sits_in() =>
        Assert.True(GeneratedContentRefresh.ClaimLease < GeneratedContentRefresh.Interval);

    /// <summary>
    /// A claim belongs to a session as much as a stamp does: the caregiver who signed out must not
    /// leave a read of their own holding a card the next one is waiting on.
    /// </summary>
    [Fact]
    public void A_claim_does_not_survive_the_session_that_took_it()
    {
        var session = new SessionGeneration();
        var schedule = Schedule(session);
        Assert.True(DueUnattended(schedule, Dad, GeneratedCard.Digest));

        session.Advance();

        Assert.True(DueUnattended(schedule, Dad, GeneratedCard.Digest));
    }

    /// <summary>
    /// A caregiver's request takes the claim over, so the pass it was taken from no longer holds
    /// the card — and must not hand back a hold that has become somebody else's, which would open
    /// the window under the read that is now out.
    /// </summary>
    [Fact]
    public void A_pass_whose_claim_was_taken_over_cannot_give_it_back()
    {
        var schedule = Schedule();
        var first = schedule.ClaimIfDue(Dad, GeneratedCard.Digest, requestedByCaregiver: false);
        Assert.True(schedule.ClaimIfDue(Dad, GeneratedCard.Digest, requestedByCaregiver: true).IsDue);

        schedule.Abandon(Dad, GeneratedCard.Digest, first);

        Assert.False(DueUnattended(schedule, Dad, GeneratedCard.Digest));
    }

    /// <summary>
    /// Nothing was claimed, so nothing is written and nothing is released. The page hands back
    /// every card it asked about, including the ones the cadence turned down.
    /// </summary>
    [Fact]
    public void Handing_back_a_card_that_was_never_claimed_does_nothing()
    {
        var schedule = Schedule();
        ReadAndRecord(schedule, Dad, GeneratedCard.Digest);

        schedule.Record(Dad, GeneratedCard.Advise, ReadClaim.None);
        schedule.Abandon(Dad, GeneratedCard.Advise, ReadClaim.None);

        Assert.True(DueUnattended(schedule, Dad, GeneratedCard.Advise));
        Assert.False(DueUnattended(schedule, Dad, GeneratedCard.Digest));
    }
}
