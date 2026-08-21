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

    private static async Task<DateOnly> FromDateForAsync(int requestedDays)
    {
        var (uow, logs) = Fakes();
        var plan = new DataQueryPlan
        {
            Sources = [DataQueryKind.RecentActivity],
            RecentActivityDays = requestedDays,
        };

        await DataQueryWhitelist.ExecuteAsync(plan, Guid.NewGuid(), uow, UtcNow, CancellationToken.None);

        var call = logs.ReceivedCalls().Single();
        return (DateOnly)call.GetArguments()[1]!;
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
    public async Task AWindowLongerThanAWeek_IsCutBackToAWeek(int requested) =>
        Assert.Equal(Today.AddDays(-7), await FromDateForAsync(requested));

    /// <summary>A shorter window the model asked for deliberately is honoured — the ceiling caps
    /// what it may take, it does not widen what it asked for.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    public async Task AShorterWindow_IsLeftAlone(int requested) =>
        Assert.Equal(Today.AddDays(-requested), await FromDateForAsync(requested));

    /// <summary>Zero or negative days would otherwise ask for a range ending before it starts.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public async Task ANonPositiveWindow_FallsToTheFloor(int requested) =>
        Assert.Equal(Today.AddDays(-1), await FromDateForAsync(requested));

    /// <summary>
    /// int.MaxValue is what a malformed plan looks like at the boundary: unclamped, the negation
    /// inside AddDays would overflow rather than simply fetching too much.
    /// </summary>
    [Fact]
    public async Task TheLargestPossibleWindow_DoesNotOverflow() =>
        Assert.Equal(Today.AddDays(-7), await FromDateForAsync(int.MaxValue));

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
        Assert.Empty(logs.ReceivedCalls());
    }
}
