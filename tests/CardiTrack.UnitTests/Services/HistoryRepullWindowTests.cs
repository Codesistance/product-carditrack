using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// The arithmetic every party to a history re-pull shares. Pinned as dates, not formulas: an
/// off-by-one here is a day silently never fetched, or a request that never completes.
/// </summary>
public class HistoryRepullWindowTests
{
    private static readonly DateOnly Today = new(2026, 9, 9);

    [Fact]
    public void Bounds_ThirtyDays_EndsYesterdayAndCoversExactlyThirty()
    {
        var (from, to) = HistoryRepullWindow.Bounds(Today, 30);

        Assert.Equal(new DateOnly(2026, 9, 8), to);
        Assert.Equal(new DateOnly(2026, 8, 10), from);
        Assert.Equal(30, to.DayNumber - from.DayNumber + 1);
    }

    [Fact]
    public void Bounds_OneDay_IsYesterdayAlone()
    {
        var (from, to) = HistoryRepullWindow.Bounds(Today, 1);

        Assert.Equal(new DateOnly(2026, 9, 8), from);
        Assert.Equal(from, to);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(91)]
    [InlineData(-5)]
    public void Bounds_RefusesDaysOutsideTheAllowedRange(int days)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => HistoryRepullWindow.Bounds(Today, days));
    }

    [Fact]
    public void NextChunk_WalksNewestFirstInSevenDayChunks_AndEndsExactlyOnFromDate()
    {
        var repull = Repull(30);
        var chunks = new List<(DateOnly From, DateOnly To)>();

        while (!HistoryRepullWindow.IsComplete(repull))
        {
            var chunk = HistoryRepullWindow.NextChunk(repull, 7);
            chunks.Add(chunk);
            repull.CompletedTo = chunk.From;
        }

        Assert.Equal(
        [
            (new DateOnly(2026, 9, 2), new DateOnly(2026, 9, 8)),
            (new DateOnly(2026, 8, 26), new DateOnly(2026, 9, 1)),
            (new DateOnly(2026, 8, 19), new DateOnly(2026, 8, 25)),
            (new DateOnly(2026, 8, 12), new DateOnly(2026, 8, 18)),
            (new DateOnly(2026, 8, 10), new DateOnly(2026, 8, 11)),
        ], chunks);
        Assert.Equal(30, HistoryRepullWindow.DaysDone(repull));
    }

    [Fact]
    public void NextChunk_RefusesACompletedRequest()
    {
        var repull = Repull(7);
        repull.CompletedTo = repull.FromDate;

        Assert.Throws<InvalidOperationException>(() => HistoryRepullWindow.NextChunk(repull, 7));
    }

    [Fact]
    public void DaysDone_CountsFromTheNewestDayInclusive()
    {
        var repull = Repull(30);
        Assert.Equal(0, HistoryRepullWindow.DaysDone(repull));

        repull.CompletedTo = new DateOnly(2026, 9, 2);
        Assert.Equal(7, HistoryRepullWindow.DaysDone(repull));
        Assert.False(HistoryRepullWindow.IsComplete(repull));

        repull.CompletedTo = new DateOnly(2026, 8, 10);
        Assert.Equal(30, HistoryRepullWindow.DaysDone(repull));
        Assert.True(HistoryRepullWindow.IsComplete(repull));
    }

    [Theory]
    [InlineData(HistoryRepullStatus.Pending, "pending")]
    [InlineData(HistoryRepullStatus.InProgress, "in_progress")]
    [InlineData(HistoryRepullStatus.Completed, "completed")]
    [InlineData(HistoryRepullStatus.Failed, "failed")]
    [InlineData(HistoryRepullStatus.Cancelled, "cancelled")]
    public void StatusWire_MapsEveryStatus(HistoryRepullStatus status, string wire)
    {
        Assert.Equal(wire, HistoryRepullWindow.StatusWire(status));
    }

    [Fact]
    public void ToResponse_SetsNextAllowedAt_OnlyForACompletedRequestInsideItsCooldown()
    {
        var now = new DateTime(2026, 9, 9, 12, 0, 0, DateTimeKind.Utc);
        var cooldown = TimeSpan.FromHours(48);

        var fresh = Repull(7);
        fresh.Status = HistoryRepullStatus.Completed;
        fresh.CompletedAt = now.AddHours(-1);
        Assert.Equal(now.AddHours(47), HistoryRepullWindow.ToResponse(fresh, cooldown, now).NextAllowedAt);

        var old = Repull(7);
        old.Status = HistoryRepullStatus.Completed;
        old.CompletedAt = now.AddHours(-49);
        Assert.Null(HistoryRepullWindow.ToResponse(old, cooldown, now).NextAllowedAt);

        // A failed request does not start a cooldown — it should be retryable at once.
        var failed = Repull(7);
        failed.Status = HistoryRepullStatus.Failed;
        failed.CompletedAt = now.AddHours(-1);
        Assert.Null(HistoryRepullWindow.ToResponse(failed, cooldown, now).NextAllowedAt);
    }

    [Fact]
    public void ShouldPresent_KeepsOpenRequests_CooledDownCompletions_AndRecentFailures()
    {
        var now = new DateTime(2026, 9, 9, 12, 0, 0, DateTimeKind.Utc);
        var cooldown = TimeSpan.FromHours(48);

        var open = Repull(7);
        open.Status = HistoryRepullStatus.InProgress;
        Assert.True(HistoryRepullWindow.ShouldPresent(open, cooldown, now));

        var completedRecently = Repull(7);
        completedRecently.Status = HistoryRepullStatus.Completed;
        completedRecently.CompletedAt = now.AddHours(-47);
        Assert.True(HistoryRepullWindow.ShouldPresent(completedRecently, cooldown, now));

        var completedLongAgo = Repull(7);
        completedLongAgo.Status = HistoryRepullStatus.Completed;
        completedLongAgo.CompletedAt = now.AddHours(-49);
        Assert.False(HistoryRepullWindow.ShouldPresent(completedLongAgo, cooldown, now));

        // A failure outlives the cooldown it never started: the caregiver has to be able to
        // find out that it did not finish.
        var failedThisWeek = Repull(7);
        failedThisWeek.Status = HistoryRepullStatus.Failed;
        failedThisWeek.CompletedAt = now.AddDays(-6);
        Assert.True(HistoryRepullWindow.ShouldPresent(failedThisWeek, cooldown, now));

        var cancelledLastMonth = Repull(7);
        cancelledLastMonth.Status = HistoryRepullStatus.Cancelled;
        cancelledLastMonth.CompletedAt = now.AddDays(-8);
        Assert.False(HistoryRepullWindow.ShouldPresent(cancelledLastMonth, cooldown, now));
    }

    [Fact]
    public void ToResponse_ReportsProgressAndTheRequestedDayCount()
    {
        var repull = Repull(30);
        repull.Status = HistoryRepullStatus.InProgress;
        repull.CompletedTo = new DateOnly(2026, 8, 26);
        repull.DaysWithData = 11;

        var response = HistoryRepullWindow.ToResponse(repull, TimeSpan.FromHours(48), DateTime.UtcNow);

        Assert.Equal("in_progress", response.Status);
        Assert.Equal(30, response.Days);
        Assert.Equal(14, response.DaysDone);
        Assert.Equal(11, response.DaysWithData);
        Assert.Equal(repull.FromDate, response.FromDate);
        Assert.Equal(repull.ToDate, response.ToDate);
    }

    private static DeviceHistoryRepull Repull(int days)
    {
        var (from, to) = HistoryRepullWindow.Bounds(Today, days);
        return new DeviceHistoryRepull { FromDate = from, ToDate = to };
    }
}
