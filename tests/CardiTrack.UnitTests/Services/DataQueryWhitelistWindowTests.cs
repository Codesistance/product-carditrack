using CardiTrack.Application.DTOs.Common;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using NSubstitute;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// The clamp is the enforcement point: the planner's number is a preference the model produces,
/// and this is the only place that decides what it actually gets. Asserting the date range that
/// reaches the repository rather than the constant itself is the point — the constant is easy to
/// change without changing what is fetched, and it is what is fetched that lands in the clinical
/// prompt and is paid for in prompt-evaluation time.
/// </summary>
public class DataQueryWhitelistWindowTests
{
    private static readonly DateTime UtcNow = new(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Today = DateOnly.FromDateTime(UtcNow);

    private static (IUnitOfWork Uow, IActivityLogRepository Logs) Fakes()
    {
        var logs = Substitute.For<IActivityLogRepository>();
        logs.GetByCardiMemberAndDateRangeAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>())
            .Returns(Array.Empty<ActivityLog>());

        var uow = Substitute.For<IUnitOfWork>();
        uow.ActivityLogs.Returns(logs);
        return (uow, logs);
    }

    /// <summary>
    /// The number of calendar days the repository was actually asked for. Both bounds are
    /// inclusive (<c>&gt;= from &amp;&amp; &lt;= to</c>), so this counts the endpoints — which is
    /// the whole point: asking for "7 days" as <c>AddDays(-7)</c> fetched eight of them.
    /// </summary>
    private static async Task<int> DaysFetchedForAsync(int requestedDays)
    {
        var (uow, logs) = Fakes();
        var plan = new DataQueryPlan
        {
            Sources = [DataQueryKind.RecentActivity],
            RecentActivityDays = requestedDays,
        };

        var fetched = await DataQueryWhitelist.ExecuteAsync(
            plan, Guid.NewGuid(), uow, UtcNow, CancellationToken.None);

        var call = logs.ReceivedCalls().Single();
        var from = (DateOnly)call.GetArguments()[1]!;
        var to = (DateOnly)call.GetArguments()[2]!;

        Assert.Equal(Today, to);
        // The window the prompt will name has to be the window that was read, or the clinical read
        // is told it is looking at a stretch nobody fetched.
        Assert.Equal((from, to), fetched.RecentActivityWindow);

        return to.DayNumber - from.DayNumber + 1;
    }

    /// <summary>
    /// Lowered from 14 days to one week on 2026-08-21. A fortnight of daily readings is twice the
    /// prompt for a CPU-served model evaluating at roughly 25 tokens/sec — measured at 47.6 s of
    /// prompt evaluation on a single chat send — and it bought an answer caregivers rarely asked
    /// for, since the planner's own default has always been 7.
    /// </summary>
    [Theory]
    [InlineData(8)]
    [InlineData(14)]
    [InlineData(30)]
    [InlineData(365)]
    public async Task AWindowLongerThanAWeek_IsCutBackToSevenDays(int requested) =>
        Assert.Equal(7, await DaysFetchedForAsync(requested));

    /// <summary>
    /// Seven days means seven, not eight. Both repository bounds are inclusive, so the original
    /// <c>AddDays(-7)</c>..<c>today</c> range spanned a week plus a day — enough that "the last
    /// week" in a caregiver's reply covered eight dates, and enough extra prompt to matter at
    /// 40 ms per token.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    public async Task AWindowInsideTheCeiling_FetchesExactlyThatManyDays(int requested) =>
        Assert.Equal(requested, await DaysFetchedForAsync(requested));

    /// <summary>One day means today alone — the floor cannot produce a range ending before it
    /// starts.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public async Task ANonPositiveWindow_FallsToASingleDay(int requested) =>
        Assert.Equal(1, await DaysFetchedForAsync(requested));

    /// <summary>
    /// int.MaxValue is what a malformed plan looks like at the boundary: unclamped, the negation
    /// inside AddDays would overflow rather than simply fetching too much.
    /// </summary>
    [Fact]
    public async Task TheLargestPossibleWindow_DoesNotOverflow() =>
        Assert.Equal(7, await DaysFetchedForAsync(int.MaxValue));

    /// <summary>
    /// The whole point of the closed source list: a plan that does not name RecentActivity reads
    /// no activity at all, whatever window it carries.
    /// </summary>
    [Fact]
    public async Task NoActivitySource_MeansNoActivityRead()
    {
        var (uow, logs) = Fakes();
        var plan = new DataQueryPlan { Sources = [], RecentActivityDays = 7 };

        var fetched = await DataQueryWhitelist.ExecuteAsync(
            plan, Guid.NewGuid(), uow, UtcNow, CancellationToken.None);

        Assert.Empty(fetched.RecentActivity);
        Assert.Null(fetched.RecentActivityWindow);
        Assert.Empty(logs.ReceivedCalls());
    }
}
