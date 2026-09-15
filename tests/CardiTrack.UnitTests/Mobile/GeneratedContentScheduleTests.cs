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

    [Fact]
    public void A_card_never_read_is_due()
    {
        var schedule = Schedule();

        Assert.True(schedule.IsDue(Dad, GeneratedCard.Digest, requestedByCaregiver: false));
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
        schedule.Record(Dad, GeneratedCard.Digest, schedule.CurrentSession);

        Advance(TimeSpan.FromSeconds(30));

        Assert.False(schedule.IsDue(Dad, GeneratedCard.Digest, requestedByCaregiver: false));
    }

    [Fact]
    public void A_read_stops_holding_once_the_interval_has_passed()
    {
        var schedule = Schedule();
        schedule.Record(Dad, GeneratedCard.Digest, schedule.CurrentSession);

        Advance(GeneratedContentRefresh.Interval);

        Assert.True(schedule.IsDue(Dad, GeneratedCard.Digest, requestedByCaregiver: false));
    }

    /// <summary>
    /// A pull or the retry button. Someone who asks is entitled to be told that nothing moved.
    /// </summary>
    [Fact]
    public void A_caregiver_who_asks_is_never_held_back()
    {
        var schedule = Schedule();
        schedule.Record(Dad, GeneratedCard.Digest, schedule.CurrentSession);

        Assert.True(schedule.IsDue(Dad, GeneratedCard.Digest, requestedByCaregiver: true));
    }

    [Fact]
    public void One_member_s_read_says_nothing_about_another_s()
    {
        var schedule = Schedule();
        schedule.Record(Dad, GeneratedCard.Digest, schedule.CurrentSession);

        Assert.True(schedule.IsDue(Mum, GeneratedCard.Digest, requestedByCaregiver: false));
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
        schedule.Record(Dad, GeneratedCard.Digest, schedule.CurrentSession);
        schedule.Record(Dad, GeneratedCard.Advise, schedule.CurrentSession);

        Assert.True(schedule.IsDue(Dad, GeneratedCard.Questions, requestedByCaregiver: false));
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
        schedule.Record(Dad, GeneratedCard.Digest, schedule.CurrentSession);
        Assert.False(schedule.IsDue(Dad, GeneratedCard.Digest, requestedByCaregiver: false));

        session.Advance();

        Assert.True(schedule.IsDue(Dad, GeneratedCard.Digest, requestedByCaregiver: false));
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
        var startedIn = schedule.CurrentSession;

        session.Advance();                 // sign-out
        session.Advance();                 // the next caregiver signs in
        _ = schedule.IsDue(Mum, GeneratedCard.Digest, requestedByCaregiver: false);

        schedule.Record(Dad, GeneratedCard.Digest, startedIn);

        Assert.True(schedule.IsDue(Dad, GeneratedCard.Digest, requestedByCaregiver: false));
    }

    [Fact]
    public void A_session_that_has_not_moved_keeps_what_it_recorded()
    {
        var session = new SessionGeneration();
        var schedule = Schedule(session);
        schedule.Record(Dad, GeneratedCard.Digest, schedule.CurrentSession);

        Assert.False(schedule.IsDue(Dad, GeneratedCard.Digest, requestedByCaregiver: false));
        Assert.False(schedule.IsDue(Dad, GeneratedCard.Digest, requestedByCaregiver: false));
    }
}
