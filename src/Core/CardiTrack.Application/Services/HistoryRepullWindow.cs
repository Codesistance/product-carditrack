using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Services;

/// <summary>
/// The date arithmetic behind a caregiver-requested history re-pull, in one place so the API
/// that records a request, the Worker that walks it and the device list that reports it cannot
/// disagree about what "30 days" or "12 of 30 done" means.
/// </summary>
/// <remarks>
/// A request covers <em>complete</em> days only: the range ends at yesterday, because today is
/// pulled every ten minutes by the routine sync already and a re-pull of a day still in progress
/// would only be overwritten. It walks newest-first, like the autonomous backfill — the recent
/// days are the ones a caregiver is looking at, so they should land first.
/// </remarks>
public static class HistoryRepullWindow
{
    /// <summary>
    /// The furthest back a caregiver may ask for. Matches the autonomous backfill's default
    /// horizon and the depth most Google Health daily data types serve; beyond it a request
    /// mostly spends quota confirming empty days.
    /// </summary>
    public const int MaxDays = 90;

    /// <summary>The wire strings the API reports a request's status as.</summary>
    public static string StatusWire(HistoryRepullStatus status) => status switch
    {
        HistoryRepullStatus.Pending => "pending",
        HistoryRepullStatus.InProgress => "in_progress",
        HistoryRepullStatus.Completed => "completed",
        HistoryRepullStatus.Failed => "failed",
        HistoryRepullStatus.Cancelled => "cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    /// <summary>The inclusive range a request for <paramref name="days"/> days covers, as of <paramref name="today"/>.</summary>
    public static (DateOnly From, DateOnly To) Bounds(DateOnly today, int days)
    {
        if (days < 1 || days > MaxDays)
            throw new ArgumentOutOfRangeException(nameof(days), days, $"Days must be between 1 and {MaxDays}.");

        var to = today.AddDays(-1);
        return (to.AddDays(-(days - 1)), to);
    }

    /// <summary>How many days the request covers in total.</summary>
    public static int TotalDays(DeviceHistoryRepull repull) =>
        repull.ToDate.DayNumber - repull.FromDate.DayNumber + 1;

    /// <summary>How many days have been fetched so far, counting from the newest.</summary>
    public static int DaysDone(DeviceHistoryRepull repull) =>
        repull.CompletedTo is { } completedTo
            ? repull.ToDate.DayNumber - completedTo.DayNumber + 1
            : 0;

    /// <summary>Whether the walk has reached the oldest day of the range.</summary>
    public static bool IsComplete(DeviceHistoryRepull repull) =>
        repull.CompletedTo is { } completedTo && completedTo <= repull.FromDate;

    /// <summary>
    /// The next stretch to fetch: up to <paramref name="chunkDays"/> days, ending just before
    /// what is already done (or at <see cref="DeviceHistoryRepull.ToDate"/> for a fresh request)
    /// and never reaching past <see cref="DeviceHistoryRepull.FromDate"/>.
    /// </summary>
    public static (DateOnly From, DateOnly To) NextChunk(DeviceHistoryRepull repull, int chunkDays)
    {
        if (chunkDays < 1)
            throw new ArgumentOutOfRangeException(nameof(chunkDays), chunkDays, "A chunk must cover at least one day.");
        if (IsComplete(repull))
            throw new InvalidOperationException("The re-pull is already complete; there is no next chunk.");

        var to = repull.CompletedTo is { } completedTo ? completedTo.AddDays(-1) : repull.ToDate;
        var floor = to.AddDays(-(chunkDays - 1));
        var from = floor > repull.FromDate ? floor : repull.FromDate;
        return (from, to);
    }

    /// <summary>
    /// The request as the API reports it. <paramref name="cooldown"/> decides whether a completed
    /// request still blocks a new one, and so whether <c>nextAllowedAt</c> is set.
    /// </summary>
    public static DeviceHistoryRepullResponse ToResponse(
        DeviceHistoryRepull repull, TimeSpan cooldown, DateTime utcNow)
    {
        DateTime? nextAllowedAt = null;
        if (repull.Status == HistoryRepullStatus.Completed
            && repull.CompletedAt is { } completedAt
            && completedAt + cooldown > utcNow)
        {
            nextAllowedAt = completedAt + cooldown;
        }

        return new DeviceHistoryRepullResponse
        {
            RepullId = repull.Id,
            Status = StatusWire(repull.Status),
            Days = TotalDays(repull),
            FromDate = repull.FromDate,
            ToDate = repull.ToDate,
            DaysDone = DaysDone(repull),
            DaysWithData = repull.DaysWithData,
            RequestedAt = repull.RequestedAt,
            StartedAt = repull.StartedAt,
            CompletedAt = repull.CompletedAt,
            NextAllowedAt = nextAllowedAt,
        };
    }
}
