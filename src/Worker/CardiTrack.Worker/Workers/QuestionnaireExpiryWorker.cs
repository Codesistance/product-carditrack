using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Enums;
using Microsoft.Extensions.Options;

namespace CardiTrack.Worker.Workers;

/// <summary>
/// Retires questions that have outlived the moment they asked about — see
/// <c>MemberQuestionnaire.AskableUntilUtc</c>.
/// </summary>
/// <remarks>
/// <para>
/// The read paths already refuse to serve a lapsed question and the apps retire one the instant they
/// see it, so this is not what keeps a stale card off a caregiver's screen. What it is for is the
/// member whose family has not opened the app: their question sits <c>Pending</c> against a day that
/// ended, and until something moves it the row is a permanent placeholder for a question that will
/// never be answered.
/// </para>
/// <para>
/// Polling a table on a schedule, which is exactly what CLAUDE.md puts here and nowhere else. It is
/// not AI work — the pipeline writes questions, this only ages them out, and the decision is a
/// timestamp comparison with no model anywhere near it.
/// </para>
/// </remarks>
public class QuestionnaireExpiryWorker : CronBackgroundService
{
    /// <summary>
    /// Rows retired in one pass. Generously above the fleet's real rate — at most one question per
    /// member per week, and only the unanswered ones reach here — so the cap is the guard against a
    /// long outage's backlog arriving as a single unbounded write, not a throttle on ordinary days.
    /// </summary>
    private const int BatchSize = 500;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<QuestionnaireExpiryWorker> _logger;

    public QuestionnaireExpiryWorker(
        IOptionsMonitor<WorkerOptions> options,
        IServiceScopeFactory scopeFactory,
        ILogger<QuestionnaireExpiryWorker> logger)
        : base(options.Get(nameof(QuestionnaireExpiryWorker)).CronExpression, logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteJobAsync(CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var lapsed = await unitOfWork.MemberQuestionnaires
            .GetLapsedPendingAsync(DateTime.UtcNow, BatchSize, stoppingToken);
        if (lapsed.Count == 0)
            return;

        foreach (var questionnaire in lapsed)
        {
            // Already tracked by GetLapsedPendingAsync — setting Status is enough. Calling Update
            // would mark every column modified and risk overwriting a concurrent answer.
            questionnaire.Status = QuestionnaireStatus.Expired;
        }

        await unitOfWork.SaveChangesAsync();

        _logger.LogInformation(
            "QuestionnaireExpiry retired {Count} questions that outlived the day they asked about.",
            lapsed.Count);
    }
}
