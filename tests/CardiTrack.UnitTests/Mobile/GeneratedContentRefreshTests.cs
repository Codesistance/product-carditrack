using CardiTrack.Mobile.Core.Offline;

namespace CardiTrack.UnitTests.Mobile;

public class GeneratedContentRefreshTests
{
    private static readonly DateTime Now = new(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_screen_that_has_never_read_them_is_due() =>
        Assert.True(GeneratedContentRefresh.IsDue(
            requestedByCaregiver: false, lastReadUtc: DateTime.MinValue, nowUtc: Now));

    [Fact]
    public void A_caregiver_who_asked_is_never_made_to_wait() =>
        Assert.True(GeneratedContentRefresh.IsDue(
            requestedByCaregiver: true, lastReadUtc: Now, nowUtc: Now));

    [Fact]
    public void An_unattended_pass_inside_the_interval_leaves_the_cards_alone() =>
        Assert.False(GeneratedContentRefresh.IsDue(
            requestedByCaregiver: false,
            lastReadUtc: Now - GeneratedContentRefresh.Interval + TimeSpan.FromSeconds(1),
            nowUtc: Now));

    [Fact]
    public void An_unattended_pass_on_the_interval_re_reads_them() =>
        Assert.True(GeneratedContentRefresh.IsDue(
            requestedByCaregiver: false,
            lastReadUtc: Now - GeneratedContentRefresh.Interval,
            nowUtc: Now));

    /// <summary>
    /// The point of the whole thing: a caregiver watching the screen for five minutes pays for
    /// ten ticks of the member's own state and one read of the cards, where every tick used to
    /// read all four.
    /// </summary>
    [Fact]
    public void One_read_of_the_cards_covers_a_run_of_ticks()
    {
        var tick = TimeSpan.FromSeconds(30);
        var lastRead = Now;
        var reads = 0;

        for (var elapsed = tick; elapsed <= GeneratedContentRefresh.Interval; elapsed += tick)
        {
            if (!GeneratedContentRefresh.IsDue(requestedByCaregiver: false, lastRead, Now + elapsed))
                continue;

            reads++;
            lastRead = Now + elapsed;
        }

        Assert.Equal(1, reads);
    }

    /// <summary>
    /// Guards the number itself, not just the comparison. Below the pipeline's five-minute
    /// assessor cadence there is provably nothing new to fetch, so shortening this would spend
    /// a watching caregiver's battery on answers that cannot have changed.
    /// </summary>
    [Fact]
    public void The_interval_is_not_shorter_than_the_pipeline_can_rewrite_them() =>
        Assert.True(GeneratedContentRefresh.Interval >= TimeSpan.FromMinutes(5));
}
