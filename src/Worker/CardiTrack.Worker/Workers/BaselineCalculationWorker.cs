using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Services;
using Microsoft.Extensions.Options;

namespace CardiTrack.Worker.Workers;

/// <summary>
/// Recalculates every active member's <c>PatternBaseline</c> rows — the statistical profile that ends
/// the "getting to know you" phase and gives the dashboard something to compare today against.
/// Daily, so a member's first baseline lands the morning after their history clears the coverage
/// gate rather than up to a week later. <see cref="BaselineCalculator"/>'s coverage gate is still
/// what decides when a member has been observed for long enough — the cron only sets how quickly
/// its verdict is picked up.
/// </summary>
public class BaselineCalculationWorker : CronBackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<BaselineCalculationWorker> _logger;

    public BaselineCalculationWorker(
        IOptionsMonitor<WorkerOptions> options,
        IServiceScopeFactory scopeFactory,
        ILogger<BaselineCalculationWorker> logger)
        : base(options.Get(nameof(BaselineCalculationWorker)).CronExpression, logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteJobAsync(CancellationToken stoppingToken)
    {
        // Yesterday, not today: the sync pulls the day in progress so the dashboard can show live
        // numbers, and a part-finished day must not be averaged into what "normal" means for this
        // member — it would pull the baseline down by however far through the day the job ran.
        var windowEnd = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1));
        var windowStart = windowEnd.AddDays(-(BaselineCalculator.SupportedPeriods.Max() - 1));

        _logger.LogInformation(
            "BaselineCalculation triggered at {Time} for the window {WindowStart}..{WindowEnd}.",
            DateTime.UtcNow, windowStart, windowEnd);

        List<Guid> memberIds;
        using (var scope = _scopeFactory.CreateScope())
        {
            var members = scope.ServiceProvider.GetRequiredService<ICardiMemberRepository>();
            memberIds = (await members.GetActiveIdsWithActivitySinceAsync(windowStart)).ToList();
        }

        var written = 0;
        var skipped = 0;
        var failureCount = 0;

        foreach (var memberId in memberIds)
        {
            if (stoppingToken.IsCancellationRequested)
                break;

            try
            {
                var (memberWritten, memberSkipped) = await CalculateForMemberAsync(memberId, windowStart, windowEnd);
                written += memberWritten;
                skipped += memberSkipped;
            }
            catch (Exception ex)
            {
                failureCount++;
                _logger.LogError(ex, "Failed to calculate baselines for CardiMember {CardiMemberId}.", memberId);
            }
        }

        _logger.LogInformation(
            "BaselineCalculation complete. Members: {Members}, baselines written: {Written}, " +
            "periods skipped for sparse data: {Skipped}, failed: {Failed}.",
            memberIds.Count, written, skipped, failureCount);
    }

    /// <summary>
    /// One scope — and so one DbContext — per member: the read tracks up to 90 log rows each, which
    /// would accumulate for the whole run on a shared context, and a member that fails takes nothing
    /// else down with it.
    /// </summary>
    private async Task<(int Written, int Skipped)> CalculateForMemberAsync(
        Guid memberId, DateOnly windowStart, DateOnly windowEnd)
    {
        using var scope = _scopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var stats = scope.ServiceProvider.GetRequiredService<IDescriptiveStatistics>();

        // Fetched once for the longest period; BaselineCalculator windows the list itself.
        var logs = (await unitOfWork.ActivityLogs
            .GetByCardiMemberAndDateRangeAsync(memberId, windowStart, windowEnd)).ToList();

        var written = 0;
        var skipped = 0;

        foreach (var periodDays in BaselineCalculator.SupportedPeriods)
        {
            var baseline = BaselineCalculator.Calculate(memberId, logs, periodDays, windowEnd, stats);
            if (baseline is null)
            {
                skipped++;
                continue;
            }

            // Appended rather than replacing the previous row: the history is what makes a shift in a
            // member's own normal visible later.
            await unitOfWork.PatternBaselines.AddAsync(baseline);
            written++;
        }

        if (written > 0)
            await unitOfWork.SaveChangesAsync();

        _logger.LogInformation(
            "Calculated baselines for CardiMember {CardiMemberId}. Written: {Written}, skipped: {Skipped}.",
            memberId, written, skipped);

        return (written, skipped);
    }
}
