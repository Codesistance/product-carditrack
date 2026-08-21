using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Services.Notifications;
using Microsoft.Extensions.Options;

namespace CardiTrack.Worker.Workers;

/// <summary>
/// Pushes a family's attention to a question the service has asked about a member — the first
/// push for a newly generated question, and a bounded run of reminders for one still unanswered.
/// </summary>
/// <remarks>
/// <para>
/// <c>DigestGenerationService</c> (run from <c>CardiTrack.PipelineJobs</c>) only ever writes the
/// <c>MemberQuestionnaire</c> row; that host deliberately has no push services registered (see
/// <c>PushServiceExtensions.AddPushServices</c>'s remarks), so it cannot dispatch one itself. A
/// sweep here — the same shape <c>NotificationDispatchWorker.EnqueuePendingNudgePushesAsync</c>
/// uses for Safety nudges — is what turns a written row into a push, and reusing the one mechanism
/// for both the original ask and every later reminder means there is only one place that decides
/// "does this question still need the family's attention".
/// </para>
/// <para>
/// <b>Claim, then send.</b> The Worker runs several Cloud Run instances and this sweep has no
/// advisory lock, so more than one instance can read the same due rows. <c>TryClaimAlertAsync</c>
/// is a conditional update keyed on the <see cref="Domain.Entities.MemberQuestionnaire.ReminderCount"/>
/// this sweep read, so only one instance's claim can win — the same discipline
/// <c>NotificationRepository.TryClaimForPushAsync</c> uses, for the same reason (see
/// notification_engine.md's incident writeup for what a claimless multi-instance sweep costs).
/// </para>
/// </remarks>
public class QuestionnaireAlertWorker : CronBackgroundService
{
    /// <summary>
    /// How long an unanswered question waits before another push. Also the gap before the very
    /// first push, in the worst case — a question generated moments after one sweep starts waits
    /// for the next tick, not this interval; this only bounds reminders.
    /// </summary>
    private static readonly TimeSpan ReminderInterval = TimeSpan.FromHours(24);

    /// <summary>
    /// Total pushes a question may receive in its lifetime — the original ask plus two reminders.
    /// A <see cref="Domain.Enums.QuestionnaireScope.Permanent"/> question has no
    /// <c>AskableUntilUtc</c> to lapse it on its own, so without a cap here it would nag a family
    /// indefinitely.
    /// </summary>
    private const int MaxPushes = 3;

    private const int BatchSize = 200;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<QuestionnaireAlertWorker> _logger;
    private readonly TimeProvider _timeProvider;

    public QuestionnaireAlertWorker(
        IOptionsMonitor<WorkerOptions> options,
        IServiceScopeFactory scopeFactory,
        ILogger<QuestionnaireAlertWorker> logger,
        TimeProvider? timeProvider = null)
        : base(options.Get(nameof(QuestionnaireAlertWorker)).CronExpression, logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    protected override async Task ExecuteJobAsync(CancellationToken stoppingToken)
    {
        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        var reminderCutoffUtc = utcNow - ReminderInterval;

        IReadOnlyList<Domain.Entities.MemberQuestionnaire> due;
        using (var readScope = _scopeFactory.CreateScope())
        {
            var unitOfWork = readScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            due = await unitOfWork.MemberQuestionnaires.GetDueForAlertAsync(
                utcNow, reminderCutoffUtc, MaxPushes, BatchSize, stoppingToken);
        }

        if (due.Count == 0)
            return;

        var enqueued = 0;

        foreach (var questionnaire in due)
        {
            if (stoppingToken.IsCancellationRequested)
                break;

            // One scope per row, matching NotificationDispatchWorker's sweep — a transient failure
            // on one question must not cost the rest of the batch.
            using var scope = _scopeFactory.CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var claimed = false;

            try
            {
                // Resolved before the claim, matching NotificationDispatchWorker's own sweep — a
                // misconfigured push stack (see notification_engine.md's FirebaseApp incident)
                // then fails here, before any row is touched, instead of claiming and immediately
                // releasing every due row on every tick.
                var dispatch = scope.ServiceProvider.GetRequiredService<IDispatchService>();

                claimed = await unitOfWork.MemberQuestionnaires.TryClaimAlertAsync(
                    questionnaire.Id, questionnaire.ReminderCount, utcNow, stoppingToken);
                if (!claimed)
                    continue;

                var deliveries = await dispatch.EnqueueForQuestionnaireAsync(
                    questionnaire.Id, questionnaire.ReminderCount, stoppingToken);

                // Nothing to a caregiver was actually delivered — no one to receive it (every
                // caregiver has ReceiveAlerts off), or the question stopped being eligible in the
                // gap between the claim and this call. The claim is given back rather than
                // counted as a push, but not to its pre-claim LastRemindedAtUtc: for a
                // never-pushed row that's null, which GetDueForAlertAsync always treats as due,
                // and restoring it would claim, enqueue nothing, and release this same row again
                // on literally the next tick — forever, if "no caregiver receives alerts" is a
                // standing account state rather than a transient one. Backing off to utcNow gives
                // it the same 24h cooldown a real reminder would, while ReminderCount still
                // reverts so the push cap isn't spent on a delivery that reached nobody.
                if (deliveries.Count == 0)
                    await ReleaseAlertClaimSafelyAsync(unitOfWork, questionnaire, utcNow, stoppingToken);
                else
                    enqueued++;
            }
            catch (Exception ex)
            {
                // "Enqueue", not "push": IDispatchService.EnqueueAsync may attempt an immediate
                // send, but the outbox and its retries are what actually govern delivery — the
                // same distinction NotificationDispatchWorker's own wording keeps.
                _logger.LogError(ex,
                    "Failed to enqueue a push for MemberQuestionnaire {QuestionnaireId} (occurrence {Occurrence}).",
                    questionnaire.Id, questionnaire.ReminderCount);

                // Unlike the empty-deliveries case above, this reverts all the way to the
                // pre-claim LastRemindedAtUtc — an enqueue failure is presumed transient (a
                // push-stack blip, a database hiccup), so the row should be eligible again on the
                // very next tick rather than backed off, the same immediate-retry stance
                // NotificationDispatchWorker's own release takes on a failed send.
                if (claimed)
                    await ReleaseAlertClaimSafelyAsync(unitOfWork, questionnaire, questionnaire.LastRemindedAtUtc, stoppingToken);
            }
        }

        if (enqueued > 0)
            _logger.LogInformation("QuestionnaireAlert enqueued {Count} question push(es).", enqueued);
    }

    /// <summary>
    /// Releases a claim whose push failed or reached nobody. Swallows its own failure
    /// deliberately, the same stance as <c>NotificationDispatchWorker.ReleasePushClaimSafelyAsync</c>:
    /// this runs inside a catch block or an already-handled empty result, and letting it throw
    /// would replace the real signal with an unrelated one. A claim that cannot be handed back
    /// simply waits out its own 24h reminder interval before trying again — worse than an
    /// immediate retry, not a stuck question.
    /// </summary>
    /// <param name="restoreLastRemindedAtUtc">
    /// What to set <see cref="Domain.Entities.MemberQuestionnaire.LastRemindedAtUtc"/> back to —
    /// the true pre-claim value for an immediate retry, or the current tick's time to back off
    /// instead. See the two call sites for which applies when.
    /// </param>
    private async Task ReleaseAlertClaimSafelyAsync(
        IUnitOfWork unitOfWork,
        Domain.Entities.MemberQuestionnaire questionnaire,
        DateTime? restoreLastRemindedAtUtc,
        CancellationToken ct)
    {
        try
        {
            await unitOfWork.MemberQuestionnaires.ReleaseAlertClaimAsync(
                questionnaire.Id, questionnaire.ReminderCount, restoreLastRemindedAtUtc, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Could not release the alert claim on MemberQuestionnaire {QuestionnaireId}; it will "
                + "not retry until its reminder interval next elapses.",
                questionnaire.Id);
        }
    }
}
