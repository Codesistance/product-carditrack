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
/// the row, so a yes sent twice at once carries it out once. The book is composed before that
/// statement and stored after it, so no model call ever runs inside a transaction — see
/// <see cref="TryResumeAsync"/>. The yes and the no are a closed vocabulary
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
/// <see cref="IDigestGenerationService.ComposeBookAsync"/>'s: the same prompt, the same guards and
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
        """ + MedicalPromptBlocks.ChatMessageGuardrail;

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

        // Written now, by compare-and-set against the offer this turn loaded the session with:
        // two destructive asks racing from the same caregiver cannot both put an offer on the row,
        // and the one that loses is told to answer the one that stands rather than overwriting it
        // with an offer whose reply arrived second.
        if (!await _unitOfWork.MemberChatSessions.TryOfferPendingActionAsync(
                session, request.Serialize(), utcNow + ConfirmationWindow, ct))
        {
            return Result(JournalChatReplies.AnotherOfferWaiting(), calls);
        }

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
    /// <para>
    /// On a yes, the book is composed first with no transaction open — a MedGemma call can take
    /// minutes, and a connection or the session row held that long would queue every other turn
    /// on the session behind it. Then one short transaction: the claim, the change, and — after
    /// this returns — the turns and the save, committed together by
    /// <see cref="MemberChatService.SendMessageAsync"/>. A failure anywhere in it rolls the whole
    /// turn back: the offer is still there, the book is as it was, and the same yes can be sent
    /// again. The book and its confirmation cannot be separated.
    /// </para>
    /// <para>
    /// The price of composing before claiming is that two yeses sent at once both generate, and
    /// the one that loses the claim discards its text. That is one wasted generation on a
    /// double-send, against a row lock across every generation otherwise — the cheaper side.
    /// </para>
    /// <para>
    /// On a no, or on any other message, the claim is autocommitted on its own, so a turn that
    /// fails afterwards still leaves the offer spent — a later yes to something else must never
    /// find it.
    /// </para>
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

        if (!JournalChatRequest.IsAffirmative(flattened))
        {
            // A no, or anything else: spend the offer on its own, then answer the no or hand the
            // message on. The claim is for the offer this request read, so a yes sent to a newer
            // offer by the same caregiver is not spent by this message's arrival.
            var spent = await _unitOfWork.MemberChatSessions.TryConsumePendingActionAsync(session, ct);
            var wasLive = spent is not null
                && JournalChatRequest.TryDeserialize(spent.Action) is not null
                && spent.ExpiresAtUtc is { } until && until > utcNow;
            return wasLive && JournalChatRequest.IsNegative(flattened)
                ? Result(JournalChatReplies.LeftAsItIs(), [])
                : null;
        }

        // A yes. What it answers is the offer the session was loaded with; the claim inside the
        // transaction below confirms nothing replaced it while the book was being composed.
        var pending = JournalChatRequest.TryDeserialize(session.PendingAction);
        var live = pending is not null && session.PendingActionExpiresAtUtc is { } expiry && expiry > utcNow;
        if (!live)
        {
            // Stale, or a line this build cannot read: spent, and the message routed as itself.
            await _unitOfWork.MemberChatSessions.TryConsumePendingActionAsync(session, ct);
            return null;
        }

        var (localToday, _) = await LocalCalendarAsync(cardiMemberId, member, utcNow);
        var firstName = NamePlaceholder.FirstName(member?.Name);

        if (!await CanManageAsync(userId, cardiMemberId, ct))
        {
            await _unitOfWork.MemberChatSessions.TryConsumePendingActionAsync(session, ct);
            return Result(JournalChatReplies.OnlyPrimaryCaregiver(firstName), []);
        }

        // The model call, before any transaction — see the remarks.
        var composition = pending!.Action == JournalChatAction.Rewrite
            ? await _books.ComposeBookAsync(cardiMemberId, pending.Audience, pending.PeriodEnd!.Value, utcNow, ct)
            : null;

        // The generation the yes paid for, whatever happens to the claim below: a book composed
        // and then not stored is still a model call this turn made, and the ledger says so.
        var generation = composition?.Usage is { } paid
            ? new List<AiCallRecord> { new(AiCallStep.JournalWrite, AiProviderSlot.Private, paid) }
            : [];

        await _unitOfWork.BeginTransactionAsync();
        var consumed = await _unitOfWork.MemberChatSessions.TryConsumePendingActionAsync(session, ct);
        if (consumed is null)
        {
            // Another request took this offer, or a newer one replaced it, while the book was
            // being composed. Nothing to carry out, and nothing to route either — the message was
            // a yes to something that is no longer on offer, so it is told that, with the
            // generation it spent billed to it. The transaction goes first.
            await _unitOfWork.RollbackTransactionAsync();
            return Result(JournalChatReplies.OfferGone(), generation);
        }

        // Checked again here, inside the transaction and after a compose that can take minutes:
        // the offer and the yes are two requests, and the caregiver may have stopped being the
        // primary one between them. The early check spares an unauthorised generation; this one
        // spares an unauthorised change.
        if (!await CanManageAsync(userId, cardiMemberId, ct))
            return Result(JournalChatReplies.OnlyPrimaryCaregiver(firstName), generation);

        return await ExecuteAsync(pending, composition, generation, cardiMemberId, firstName, localToday, ct);
    }

    /// <summary>
    /// The change itself, inside the transaction <see cref="TryResumeAsync"/> opened: a discard is
    /// one delete; a rewrite stores the composition made before the transaction, or explains why
    /// there is none.
    /// </summary>
    private async Task<MemberChatWorkflowResult> ExecuteAsync(
        JournalChatRequest request,
        JournalRewriteResult? composition,
        IReadOnlyList<AiCallRecord> generation,
        Guid cardiMemberId,
        string? firstName,
        DateOnly localToday,
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

        var result = composition ?? throw new InvalidOperationException("A rewrite reaches execution with its composition.");
        var calls = generation;

        if (result.Outcome == JournalRewriteOutcome.Written)
        {
            var (removed, inserted) = await _unitOfWork.Digests.ReplaceBookAsync(result.Entry!, ct);
            var stored = result.Entry!;
            if (!inserted)
            {
                // The due pass landed a book for the same period between the delete and the
                // insert. Its account is as good as ours and already stored; report that one.
                _logger.LogInformation(
                    "The rewrite of the {Audience} for CardiMember {CardiMemberId} dated {PeriodEnd} lost the insert "
                    + "to a concurrent write; serving the stored book.",
                    request.Audience, cardiMemberId, periodEnd);
                stored = await _unitOfWork.Digests.GetLatestByDateAsync(cardiMemberId, periodEnd, request.Audience, ct) ?? stored;
            }

            _logger.LogInformation(
                "Rewrote the {Audience} for CardiMember {CardiMemberId} dated {PeriodEnd} at a caregiver's request "
                + "({Removed} earlier row(s) removed).",
                request.Audience, cardiMemberId, periodEnd, removed);

            return Result(
                JournalChatReplies.Written(result with { Entry = stored, ReplacedAnEarlierBook = removed > 0 }, localToday),
                calls);
        }

        // Refused, or nothing to write from. The reply says what stands for the period *now*: a
        // composition can take a minute, and the state when the yes arrived is not the state now.
        var hasABook = await _unitOfWork.Digests.GetLatestByDateAsync(cardiMemberId, periodEnd, request.Audience, ct) is not null;

        _logger.LogInformation(
            "Rewrite of the {Audience} for CardiMember {CardiMemberId} dated {PeriodEnd} at a caregiver's request: {Outcome}.",
            request.Audience, cardiMemberId, periodEnd, result.Outcome);

        return Result(JournalChatReplies.NotWritten(result, request, hasABook, firstName, localToday), calls);
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

        // The history is framed as untrusted text whenever it is present, the way the message
        // always is — and after the data, as the router does, so nothing the caregiver wrote is the
        // last thing the model reads.
        var guardHistory = questionsOnlyHistory is null ? string.Empty : MedicalPromptBlocks.ChatHistoryGuardrail;

        return $"""
            {ResolveInstructions}

            Today is {localToday.ToString("dddd yyyy-MM-dd", CultureInfo.InvariantCulture)}. The journal's
            weeks start on {weekStartsOn}.
            {historySection}

            --- {MedicalPromptBlocks.ChatQuestionLabel} ---
            {question}
            """ + guardHistory;
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
