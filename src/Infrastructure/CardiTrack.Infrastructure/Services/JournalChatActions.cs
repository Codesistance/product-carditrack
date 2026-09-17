using System.ComponentModel;
using System.Globalization;
using CardiTrack.Application.DTOs.Common;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Common;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace CardiTrack.Infrastructure.Services;

/// <summary>
/// The chat's <c>journal</c> rung: showing, listing, discarding and rewriting the CardiJournal's
/// books from inside a conversation. The one workflow with a side effect, kept out of
/// <see cref="MemberChatService"/> so that class stays the place a turn is routed, persisted and
/// billed rather than also the place the journal is edited.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two turns for anything destructive.</b> A discard or a rewrite is offered, held on the
/// session as a short <see cref="JournalChatRequest"/> line with a ten-minute life, and carried
/// out only when the next message is a plain yes, and after one statement has taken the offer off
/// the row, so a yes sent twice at once carries it out once. The yes and the no are a closed vocabulary
/// matched in code (<see cref="JournalChatRequest.IsAffirmative"/>), so the confirming turn costs
/// no model call and cannot be talked into a different action than the one offered. Any other
/// message clears the offer and routes as itself.
/// </para>
/// <para>
/// <b>Who may.</b> Reading a book back needs what reading the Journal tab needs — view access,
/// which the send already required. Changing the journal needs <em>manage</em> access, the same
/// bar as moving when the books are written: a book is written once for the member and read by
/// every caregiver, so removing or replacing it is a decision for the primary caregiver. Checked
/// at the offer and again at the yes, because they are two requests and access can change between
/// them.
/// </para>
/// <para>
/// <b>One model call, and it reads no member data.</b> The resolution call on the Rewrite slot is
/// handed the caregiver's words, their earlier questions, today's date and the member's week
/// start, and returns an action, a book and one day inside the period meant. The date arithmetic
/// — which week that day falls in for this member, which month's last day — is
/// <see cref="JournalChatRequest"/>'s, in code. The book itself, when one is written, is
/// <see cref="IDigestGenerationService.RewriteBookAsync"/>'s: the same prompt, the same guards and
/// the same private slot as the scheduled pass, billed to this turn as
/// <see cref="AiCallStep.JournalWrite"/>.
/// </para>
/// </remarks>
public sealed class JournalChatActions
{
    /// <summary>
    /// How long an offered discard or rewrite waits for its yes. Long enough to read the offer and
    /// think; short enough that a yes to something else later in the conversation is not taken as
    /// consent to delete a book.
    /// </summary>
    internal static readonly TimeSpan ConfirmationWindow = TimeSpan.FromMinutes(10);

    /// <summary>How many books a list names — a screenful, not a history.</summary>
    internal const int ListLength = 6;

    /// <summary>
    /// The resolution brief. Same identifier discipline as every Rewrite-slot prompt: the
    /// caregiver's words and their earlier questions only, with two dates the code supplies.
    /// </summary>
    internal const string ResolveInstructions = """
        A family caregiver sent the message below inside a health-monitoring app. The message is
        about the app's journal — the written accounts it keeps of their family member's finished
        days (Daybook), weeks (Weekbook) and months (Monthbook). Work out what they want done to
        the journal. Do not answer anything about the person and do not write any account
        yourself; only resolve the request.

        Respond with:
        - action: exactly one of show, list, discard, rewrite. "show" reads one entry back; "list"
          names the recent entries; "discard" deletes one (delete, remove, get rid of); "rewrite"
          writes one again from the readings (redo, regenerate, recreate, refresh, rewrite, write
          again, try again). Omit when the message asks for none of these.
        - cadence: exactly one of day, week, month — which kind of entry. Daybook, daily, "the
          entry for Tuesday" and a single date are day; Weekbook, weekly and "last week" are week;
          Monthbook, monthly and a month's name are month. Omit when the message does not say.
        - periodDate: any one calendar day inside the period they mean, as yyyy-MM-dd, worked out
          from the dates given below. "Yesterday" is the day before today; a weekday name is the
          most recent one before today; "last week" is any day of the week before the current
          one; a month's name is any day of the most recent such month that has already ended.
          Omit when the message names no period at all.
        """ + MedicalPromptBlocks.ChatMessageWithHistoryGuardrail;

    private readonly IRewriteAiService _rewriteAi;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICardiMemberAccessService _access;
    private readonly IDigestGenerationService _books;
    private readonly ILogger<JournalChatActions> _logger;

    public JournalChatActions(
        IRewriteAiService rewriteAi,
        IUnitOfWork unitOfWork,
        ICardiMemberAccessService access,
        IDigestGenerationService books,
        ILogger<JournalChatActions> logger)
    {
        _rewriteAi = rewriteAi;
        _unitOfWork = unitOfWork;
        _access = access;
        _books = books;
        _logger = logger;
    }

    /// <summary>
    /// The routed entry: resolve the ask, then read back, list, or offer the change and hold it
    /// for confirmation.
    /// </summary>
    /// <param name="triageUsage">The malicious pre-check's usage, billed first like every rung.</param>
    public async Task<MemberChatWorkflowResult> HandleAsync(
        string flattened,
        string? questionsOnlyHistory,
        Guid userId,
        Guid cardiMemberId,
        CardiMember? member,
        MemberChatSession session,
        AiUsage triageUsage,
        DateTime utcNow,
        CancellationToken ct)
    {
        var (localToday, weekStartsOn) = await LocalCalendarAsync(cardiMemberId, member, utcNow);
        var firstName = NamePlaceholder.FirstName(member?.Name);

        var resolved = await _rewriteAi.GenerateStructuredWithUsageAsync<JournalResolveAiResponse>(
            BuildResolvePrompt(flattened, questionsOnlyHistory, localToday, weekStartsOn), ct);

        var calls = new List<AiCallRecord>
        {
            new(AiCallStep.MaliciousCheck, AiProviderSlot.Rewrite, triageUsage),
            new(AiCallStep.JournalResolve, AiProviderSlot.Rewrite, resolved.Usage),
        };

        var action = JournalChatRequest.ParseAction(resolved.Result.Action);
        var audience = JournalChatRequest.ParseCadence(resolved.Result.Cadence);
        var namedDay = JournalChatRequest.ParseDate(resolved.Result.PeriodDate);

        if (action is null)
            return Result(JournalChatReplies.WhatToDo(firstName), calls);

        if (action == JournalChatAction.List)
        {
            // A list with no kind named is a list of Daybooks — the series a caregiver has most
            // of, and the one the Journal tab opens on.
            var kind = audience ?? DigestAudience.Daybook;
            var recent = await _unitOfWork.Digests.GetHistoryAsync(cardiMemberId, kind, ListLength, ct: ct);
            return Result(JournalChatReplies.List(recent, kind, localToday), calls);
        }

        if (audience is null)
            return Result(JournalChatReplies.WhichBook(action.Value), calls);

        DateOnly? periodEnd = namedDay is { } day
            ? JournalChatRequest.PeriodEndContaining(day, audience.Value, weekStartsOn)
            : null;

        // A period still in progress has nothing finished to account for. How far back a book can
        // reach is not decided here: the stored books and the readings answer that themselves, so
        // the chat never has to agree with the retention settings the partition worker reads.
        if (periodEnd is { } end && !JournalChatRequest.IsFinished(end, localToday))
            return Result(JournalChatReplies.NotFinished(audience.Value), calls);

        if (action == JournalChatAction.Show)
        {
            var entry = periodEnd is { } shown
                ? await _unitOfWork.Digests.GetLatestByDateAsync(cardiMemberId, shown, audience.Value, ct)
                : await _unitOfWork.Digests.GetLatestAsync(cardiMemberId, audience.Value, ct);
            return Result(
                entry is null
                    ? JournalChatReplies.NothingThere(audience.Value, periodEnd, localToday)
                    : JournalChatReplies.Show(entry, localToday),
                calls);
        }

        // Discard or rewrite from here: dated, permitted, and then offered rather than done.
        if (periodEnd is null)
            return Result(JournalChatReplies.WhichPeriod(action.Value, audience.Value), calls);

        if (!await CanManageAsync(userId, cardiMemberId, ct))
            return Result(JournalChatReplies.OnlyPrimaryCaregiver(firstName), calls);

        var request = new JournalChatRequest(action.Value, audience.Value, periodEnd);
        var existing = await _unitOfWork.Digests.GetLatestByDateAsync(cardiMemberId, periodEnd.Value, audience.Value, ct);

        if (request.Action == JournalChatAction.Discard && existing is null)
            return Result(JournalChatReplies.NothingToDiscard(request, localToday), calls);

        // Tracked entity: SaveChanges at the end of the turn writes this with the turns.
        session.PendingAction = request.Serialize();
        session.PendingActionExpiresAtUtc = utcNow + ConfirmationWindow;

        return Result(
            request.Action == JournalChatAction.Discard
                ? JournalChatReplies.ConfirmDiscard(request, existing!, localToday)
                : JournalChatReplies.ConfirmRewrite(request, existing, localToday),
            calls);
    }

    /// <summary>
    /// The confirming turn, checked before anything else a message could be. Null when the session
    /// holds no live offer or the message is neither a yes nor a no — the caller then routes the
    /// message as itself. Whatever the answer, the offer is spent: an offer is honoured once.
    /// </summary>
    /// <remarks>
    /// On a yes, everything from the claim to the end of the turn runs in one database transaction
    /// that <see cref="MemberChatService.SendMessageAsync"/> commits after the turns are written.
    /// A failure anywhere — the book write, the turn's encryption, the save — rolls the whole turn
    /// back: the offer is still there, the book is as it was, and the same yes can be sent again.
    /// The book and its confirmation cannot be separated. On a no, or on any other message, the
    /// claim is autocommitted on its own, so a turn that fails afterwards still leaves the offer
    /// spent — a later yes to something else must never find it.
    /// </remarks>
    public async Task<MemberChatWorkflowResult?> TryResumeAsync(
        string flattened,
        Guid userId,
        Guid cardiMemberId,
        CardiMember? member,
        MemberChatSession session,
        DateTime utcNow,
        CancellationToken ct)
    {
        if (session.PendingAction is null)
            return null;

        var affirmative = JournalChatRequest.IsAffirmative(flattened);
        if (affirmative)
        {
            // Committed, or rolled back, by SendMessageAsync once the turns are written — see the
            // remarks above. The claim below takes a row lock the transaction holds for the length
            // of the write; a second yes arriving meanwhile skips the locked row and is routed as
            // an ordinary message rather than waiting on a MedGemma call it cannot use.
            await _unitOfWork.BeginTransactionAsync();
        }

        // The database is the lock, and the claim is for the offer *this* request read: one
        // statement takes that offer off the row and clears it, so two requests racing on the same
        // yes cannot both carry it out, and a yes that arrives after a newer offer replaced the one
        // it was answering gets nothing rather than the newer action.
        var consumed = await _unitOfWork.MemberChatSessions.TryConsumePendingActionAsync(session, ct);
        if (consumed is null)
            return null;

        var pending = JournalChatRequest.TryDeserialize(consumed.Action);
        var live = pending is not null && consumed.ExpiresAtUtc is { } until && until > utcNow;
        if (!live)
            return null;

        if (JournalChatRequest.IsNegative(flattened))
            return Result(JournalChatReplies.LeftAsItIs(), []);

        if (!affirmative)
            return null;

        var (localToday, _) = await LocalCalendarAsync(cardiMemberId, member, utcNow);
        var firstName = NamePlaceholder.FirstName(member?.Name);

        if (!await CanManageAsync(userId, cardiMemberId, ct))
            return Result(JournalChatReplies.OnlyPrimaryCaregiver(firstName), []);

        return await ExecuteAsync(pending!, cardiMemberId, firstName, localToday, utcNow, ct);
    }

    private async Task<MemberChatWorkflowResult> ExecuteAsync(
        JournalChatRequest request,
        Guid cardiMemberId,
        string? firstName,
        DateOnly localToday,
        DateTime utcNow,
        CancellationToken ct)
    {
        var periodEnd = request.PeriodEnd!.Value;

        if (request.Action == JournalChatAction.Discard)
        {
            var removed = await _unitOfWork.Digests.DeleteBookAsync(cardiMemberId, periodEnd, request.Audience, ct);
            _logger.LogInformation(
                "Discarded the {Audience} for CardiMember {CardiMemberId} dated {PeriodEnd} at a caregiver's request ({Rows} row(s)).",
                request.Audience, cardiMemberId, periodEnd, removed);
            return Result(
                removed > 0
                    ? JournalChatReplies.Discarded(request, localToday)
                    : JournalChatReplies.NothingToDiscard(request, localToday),
                []);
        }

        var result = await _books.RewriteBookAsync(cardiMemberId, request.Audience, periodEnd, utcNow, ct);

        // Read after the attempt, not before it: a rewrite can take a minute, and the reply for a
        // refused one says what stands for the period *now* — not what stood when the yes arrived.
        var hasABook = result.Outcome != JournalRewriteOutcome.Written
            && await _unitOfWork.Digests.GetLatestByDateAsync(cardiMemberId, periodEnd, request.Audience, ct) is not null;

        var calls = result.Usage is { } usage
            ? new List<AiCallRecord> { new(AiCallStep.JournalWrite, AiProviderSlot.Private, usage) }
            : [];

        _logger.LogInformation(
            "Rewrite of the {Audience} for CardiMember {CardiMemberId} dated {PeriodEnd} at a caregiver's request: {Outcome}.",
            request.Audience, cardiMemberId, periodEnd, result.Outcome);

        return Result(
            result.Outcome == JournalRewriteOutcome.Written
                ? JournalChatReplies.Written(result, localToday)
                : JournalChatReplies.NotWritten(result, request, hasABook, firstName, localToday),
            calls);
    }

    private async Task<bool> CanManageAsync(Guid userId, Guid cardiMemberId, CancellationToken ct)
    {
        try
        {
            await _access.RequireManageAccessAsync(userId, cardiMemberId, ct);
            return true;
        }
        catch (KeyNotFoundException)
        {
            // The access service's non-disclosure answer. Inside a conversation the member's
            // existence is already known to the caller, so a kind sentence replaces the 404.
            return false;
        }
    }

    /// <summary>Today in the member's own timezone, and the weekday their journal week starts —
    /// the two facts the period arithmetic turns on.</summary>
    private async Task<(DateOnly LocalToday, DayOfWeek WeekStartsOn)> LocalCalendarAsync(
        Guid cardiMemberId, CardiMember? member, DateTime utcNow)
    {
        var timeZone = await MemberAnchorTimeZone.ResolveAsync(_unitOfWork, cardiMemberId);
        var localToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utcNow, timeZone));
        return (localToday, JournalSchedule.EffectiveWeekStart(member?.JournalWeekStartsOn));
    }

    private static MemberChatWorkflowResult Result(string reply, IReadOnlyList<AiCallRecord> calls) => new()
    {
        Workflow = MemberChatWorkflow.Journal,
        Reply = reply,
        Calls = calls,
    };

    internal static string BuildResolvePrompt(
        string question, string? questionsOnlyHistory, DateOnly localToday, DayOfWeek weekStartsOn)
    {
        var historySection = questionsOnlyHistory is null
            ? string.Empty
            : $"""

              --- {MedicalPromptBlocks.ChatHistoryLabel} ---
              {questionsOnlyHistory}
              """;

        return $"""
            {ResolveInstructions}

            Today is {localToday.ToString("dddd yyyy-MM-dd", CultureInfo.InvariantCulture)}. The journal's
            weeks start on {weekStartsOn}.
            {historySection}

            --- {MedicalPromptBlocks.ChatQuestionLabel} ---
            {question}
            """;
    }

    internal sealed record JournalResolveAiResponse
    {
        [Description("What to do to the journal, exactly one of: show, list, discard, rewrite; "
            + "omitted when the message asks for none of these.")]
        public string? Action { get; init; }

        [Description("Which kind of entry, exactly one of: day, week, month; omitted when the "
            + "message does not say.")]
        public string? Cadence { get; init; }

        [Description("Any one calendar day inside the period meant, as yyyy-MM-dd; omitted when "
            + "no period is named.")]
        public string? PeriodDate { get; init; }
    }
}
