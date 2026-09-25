using System.Globalization;
using CardiTrack.Application.DTOs.Common;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// <see cref="MemberChatService.SendMessageAsync"/> driven through the journal rung: reading a
/// book back, listing, and the two-turn shape of a discard or a rewrite — offered, held on the
/// session, and carried out only on a plain yes from someone allowed to.
/// </summary>
/// <remarks>
/// Asserted at the service boundary rather than on <see cref="JournalChatActions"/> alone, for
/// the reason the advise suite gives: the thing that would actually break is the wiring — the
/// yes reaching the router instead of the offer, the offer surviving a no, a view-only
/// caregiver's yes deleting a book — and none of that is visible from the handler's own tests.
/// </remarks>
public class MemberChatJournalRungTests
{
    private readonly IMedicalAiService _medicalAi = Substitute.For<IMedicalAiService>();
    private readonly IRewriteAiService _rewriteAi = Substitute.For<IRewriteAiService>();
    private readonly IDataQueryPlanner _planner = Substitute.For<IDataQueryPlanner>();
    private readonly IChatRouter _router = Substitute.For<IChatRouter>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly ICardiMemberAccessService _access = Substitute.For<ICardiMemberAccessService>();
    private readonly IDigestGenerationService _books = Substitute.For<IDigestGenerationService>();

    private readonly IDigestRepository _digests = Substitute.For<IDigestRepository>();
    private readonly IMemberChatSessionRepository _sessions = Substitute.For<IMemberChatSessionRepository>();
    private readonly IMemberChatTurnUsageRepository _usages = Substitute.For<IMemberChatTurnUsageRepository>();

    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _memberId = Guid.NewGuid();

    /// <summary>The member's local today — UTC, since the test member has no anchored caregiver.</summary>
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    /// <summary>A finished day a few days back, the subject of most asks below.</summary>
    private static readonly DateOnly Reviewed = Today.AddDays(-3);

    /// <summary>The session the first turn creates, handed back as the active one on the next.</summary>
    private MemberChatSession? _session;

    public MemberChatJournalRungTests()
    {
        _unitOfWork.CardiMembers.Returns(Substitute.For<ICardiMemberRepository>());
        _unitOfWork.MemberChatSessions.Returns(_sessions);
        _unitOfWork.MemberChatTurns.Returns(Substitute.For<IMemberChatTurnRepository>());
        _unitOfWork.MemberChatTurnUsages.Returns(_usages);
        _unitOfWork.Digests.Returns(_digests);
        _unitOfWork.UserCardiMembers.Returns(Substitute.For<IUserCardiMemberRepository>());
        _unitOfWork.UserCardiMembers.GetByCardiMemberIdAsync(_memberId).Returns(Array.Empty<UserCardiMember>());

        _unitOfWork.CardiMembers.GetByIdAsync(_memberId).Returns(new CardiMember
        {
            Id = _memberId,
            FirstName = "Moses",
            LastName = "Doe",
            DateOfBirth = new DateOnly(1948, 3, 15),
            IsActive = true,
        });

        // The first turn finds no session and creates one; later turns get that same instance,
        // which is where the pending offer lives.
        _sessions.GetActiveAsync(_userId, _memberId, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(_ => _session);
        _sessions.When(r => r.AddAsync(Arg.Any<MemberChatSession>()))
            .Do(call => _session = call.Arg<MemberChatSession>());

        // The claim-and-clear statement, against the in-memory session: hands the offer back
        // once, then nothing — the same contract the real one holds against the row.
        _sessions.TryConsumePendingActionAsync(Arg.Any<MemberChatSession>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var s = call.Arg<MemberChatSession>();
                if (s.PendingAction is not { } action)
                    return null;
                var consumed = new PendingChatAction(action, s.PendingActionExpiresAtUtc);
                s.PendingAction = null;
                s.PendingActionExpiresAtUtc = null;
                return consumed;
            });

        _rewriteAi.GenerateStructuredWithUsageAsync<MemberChatService.MaliciousCheckAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new AiGenerationResult<MemberChatService.MaliciousCheckAiResponse>(
                new MemberChatService.MaliciousCheckAiResponse
                {
                    IsMalicious = false,
                    IsCasualOrSocial = false,
                    IsOffTopic = false,
                    IsAboutThisMoment = false,
                    IsAskingForAdvice = false,
                },
                new AiUsage { ModelName = "test-rewrite" }));

        _router.RouteAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new AiGenerationResult<ChatRouteDecision>(
                new ChatRouteDecision { Primary = MemberChatWorkflow.Journal },
                new AiUsage { ModelName = "test-router" }));

        _digests.GetLatestByDateAsync(_memberId, Arg.Any<DateOnly>(), Arg.Any<DigestAudience>(), Arg.Any<CancellationToken>())
            .Returns((DigestEntry?)null);

        // The store behind a confirmed rewrite: an earlier book removed, the new one landed.
        _digests.ReplaceBookAsync(Arg.Any<DigestEntry>(), Arg.Any<CancellationToken>()).Returns((1, true));

        // The compare-and-set offer, against the in-memory session: lands, as it does on a row
        // nobody else has written to.
        _sessions.TryOfferPendingActionAsync(
                Arg.Any<MemberChatSession>(), Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var s = call.Arg<MemberChatSession>();
                s.PendingAction = call.ArgAt<string>(1);
                s.PendingActionExpiresAtUtc = call.ArgAt<DateTime>(2);
                return true;
            });
    }

    private void Resolves(string? action, string? cadence, DateOnly? day) =>
        _rewriteAi.GenerateStructuredWithUsageAsync<JournalChatActions.JournalResolveAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new AiGenerationResult<JournalChatActions.JournalResolveAiResponse>(
                new JournalChatActions.JournalResolveAiResponse
                {
                    Action = action,
                    Cadence = cadence,
                    PeriodDate = day?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                },
                new AiUsage { ModelName = "test-rewrite" }));

    private DigestEntry StoredDaybook(DateOnly date, string headline = "A settled day") => new()
    {
        CardiMemberId = _memberId,
        LocalDate = date,
        Audience = DigestAudience.Daybook,
        Headline = headline,
        Text = "Moses slept a little longer than usual and was up and about by mid-morning.",
        GeneratedAtUtc = DateTime.UtcNow.AddDays(-2),
    };

    private void HasDaybook(DateOnly date) =>
        _digests.GetLatestByDateAsync(_memberId, date, DigestAudience.Daybook, Arg.Any<CancellationToken>())
            .Returns(StoredDaybook(date));

    private void ViewOnlyCaregiver() =>
        _access.RequireManageAccessAsync(_userId, _memberId, Arg.Any<CancellationToken>())
            .ThrowsAsync(new KeyNotFoundException("denied"));

    private MemberChatService CreateSut() =>
        new(_medicalAi, _rewriteAi, _planner, _router,
            Substitute.For<IAlertChangePlanner>(), Substitute.For<IAlertPreferenceService>(),
            Substitute.For<IMetricAlarmService>(), _unitOfWork, _access,
            PromptContextFactory.Composer(_unitOfWork), PromptContextFactory.Encryption,
            new JournalChatActions(_rewriteAi, _unitOfWork, _access, _books, new PassThroughWriteGuard(), NullLogger<JournalChatActions>.Instance),
            new PassThroughWriteGuard(),
            NullLogger<MemberChatService>.Instance);

    private Task<MemberChatMessageResponse> Send(string message) =>
        CreateSut().SendMessageAsync(_userId, _memberId, message);

    // ── Reading back ────────────────────────────────────────────────────────

    [Fact]
    public async Task Showing_a_daybook_reads_the_stored_book_back()
    {
        Resolves("show", "day", Reviewed);
        HasDaybook(Reviewed);

        var result = await Send("what did the daybook say for the other day");

        Assert.Contains("A settled day", result.Reply, StringComparison.Ordinal);
        Assert.Contains("slept a little longer than usual", result.Reply, StringComparison.Ordinal);
        Assert.False(result.ChangedJournal);
        await _access.DidNotReceive().RequireManageAccessAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The member's name never reaches the Rewrite slot: the resolver sees the caregiver's words
    /// with the name replaced by the placeholder, as every Rewrite-slot prompt does.
    /// </summary>
    [Fact]
    public async Task The_resolver_never_sees_the_members_name()
    {
        Resolves("show", "day", Reviewed);
        HasDaybook(Reviewed);

        await Send("show me Moses's daybook from the other day");

        await _rewriteAi.Received(1).GenerateStructuredWithUsageAsync<JournalChatActions.JournalResolveAiResponse>(
            Arg.Is<string>(prompt => !prompt.Contains("Moses", StringComparison.Ordinal)), Arg.Any<CancellationToken>());
    }

    /// <summary>How far back a book can reach is the data's to answer, not a cutoff's: a day twenty
    /// years back is looked up like any other and found missing.</summary>
    [Fact]
    public async Task A_day_long_ago_is_looked_up_not_refused()
    {
        var longAgo = Today.AddYears(-20);
        Resolves("show", "day", longAgo);

        var result = await Send("show me the daybook from twenty years ago");

        Assert.Contains("There's no Daybook for", result.Reply, StringComparison.Ordinal);
        await _digests.Received(1).GetLatestByDateAsync(_memberId, longAgo, DigestAudience.Daybook, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Showing_a_day_with_no_book_says_so_and_offers_to_write_one()
    {
        Resolves("show", "day", Reviewed);

        var result = await Send("show me that day's daybook");

        Assert.Contains("There's no Daybook for", result.Reply, StringComparison.Ordinal);
        Assert.Contains("ask me to write one", result.Reply, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Listing_names_the_recent_books_of_that_kind()
    {
        Resolves("list", "week", null);
        _digests.GetHistoryAsync(_memberId, DigestAudience.Weekbook, JournalChatActions.ListLength, ct: Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<DigestEntry>)
            [
                new DigestEntry { LocalDate = Today.AddDays(-4), Audience = DigestAudience.Weekbook, Headline = "A steadier week" },
                new DigestEntry { LocalDate = Today.AddDays(-11), Audience = DigestAudience.Weekbook, Headline = "Less sleep than usual" },
            ]);

        var result = await Send("which weekbooks are there");

        Assert.Contains("A steadier week", result.Reply, StringComparison.Ordinal);
        Assert.Contains("Less sleep than usual", result.Reply, StringComparison.Ordinal);
    }

    /// <summary>
    /// The read-back rungs never reach the planner, the clinical read or the rewrite — the book
    /// is the answer, and re-reading the readings would be a second account of the same day.
    /// </summary>
    [Fact]
    public async Task The_journal_rung_never_reads_the_readings_itself()
    {
        Resolves("show", "day", Reviewed);
        HasDaybook(Reviewed);

        await Send("show me the daybook");

        await _planner.DidNotReceive().PlanAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyList<DataQueryKind>?>(), Arg.Any<CancellationToken>());
        await _medicalAi.DidNotReceiveWithAnyArgs()
            .GenerateStructuredWithUsageAsync<MemberChatService.MemberChatClinicalAiResponse>(default!, default);
    }

    [Fact]
    public async Task A_resolved_turn_bills_the_triage_the_route_and_the_resolution()
    {
        Resolves("show", "day", Reviewed);
        HasDaybook(Reviewed);

        await Send("show me the daybook");

        await _usages.Received(1).AddAsync(Arg.Is<MemberChatTurnUsage>(u => u.Step == AiCallStep.MaliciousCheck));
        await _usages.Received(1).AddAsync(Arg.Is<MemberChatTurnUsage>(u => u.Step == AiCallStep.Route));
        await _usages.Received(1).AddAsync(Arg.Is<MemberChatTurnUsage>(u =>
            u.Step == AiCallStep.JournalResolve && u.ProviderSlot == AiProviderSlot.Rewrite));
    }

    // ── Offering a change ───────────────────────────────────────────────────

    [Fact]
    public async Task A_rewrite_is_offered_not_done_and_held_on_the_session()
    {
        Resolves("rewrite", "day", Reviewed);
        HasDaybook(Reviewed);

        var result = await Send("rewrite that day's daybook");

        Assert.Contains("replacing the one there now", result.Reply, StringComparison.Ordinal);
        Assert.Contains("Shall I go ahead?", result.Reply, StringComparison.Ordinal);
        Assert.Equal($"Rewrite|Daybook|{Reviewed:yyyy-MM-dd}", _session!.PendingAction);
        Assert.NotNull(_session.PendingActionExpiresAtUtc);
        await _books.DidNotReceiveWithAnyArgs().ComposeBookAsync(default, default, default, default, default);
    }

    /// <summary>
    /// A day with no book yet can still be written — the days the truncation migration emptied
    /// are exactly the ones a caregiver will ask about — and the offer says so rather than
    /// pretending to replace something.
    /// </summary>
    [Fact]
    public async Task A_rewrite_of_a_missing_book_offers_to_write_one()
    {
        Resolves("rewrite", "day", Reviewed);

        var result = await Send("write the daybook for that day");

        Assert.Contains("There's no Daybook for", result.Reply, StringComparison.Ordinal);
        Assert.Contains("Shall I write one", result.Reply, StringComparison.Ordinal);
        Assert.StartsWith("Rewrite|Daybook|", _session!.PendingAction, StringComparison.Ordinal);
    }

    /// <summary>
    /// Two destructive asks racing from the same caregiver: the second finds the row already
    /// holding an offer and is told to answer it, rather than overwriting it with an offer whose
    /// reply arrived second.
    /// </summary>
    [Fact]
    public async Task A_second_offer_that_loses_the_write_is_told_to_answer_the_first()
    {
        Resolves("discard", "day", Reviewed);
        HasDaybook(Reviewed);
        _sessions.TryOfferPendingActionAsync(
                Arg.Any<MemberChatSession>(), Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await Send("delete that daybook");

        Assert.Contains("already a change waiting", result.Reply, StringComparison.Ordinal);
        Assert.Null(_session!.PendingAction);
    }

    /// <summary>A date at the calendar's very edge cannot be stepped to a week end without
    /// overflowing; it is dropped before the arithmetic, and the turn asks which period was meant.
    /// (A merely far-future date gets the ordinary unfinished-period reply instead.)</summary>
    [Fact]
    public async Task A_date_far_outside_the_journal_is_treated_as_no_date()
    {
        Resolves("rewrite", "week", new DateOnly(9999, 12, 30));

        var result = await Send("rewrite the weekbook");

        Assert.Contains("Which Weekbook should I write again", result.Reply, StringComparison.Ordinal);
        Assert.Null(_session!.PendingAction);
    }

    [Fact]
    public async Task A_discard_of_a_missing_book_has_nothing_to_offer()
    {
        Resolves("discard", "day", Reviewed);

        var result = await Send("delete that daybook");

        Assert.Contains("to delete", result.Reply, StringComparison.Ordinal);
        Assert.Null(_session!.PendingAction);
    }

    /// <summary>
    /// The model names any day inside the week; the code dates the offer by the member's own
    /// week end — a Monday-start member's week containing a Thursday ends on the Sunday after.
    /// </summary>
    [Fact]
    public async Task A_week_is_dated_by_the_members_week_end()
    {
        var thursday = Today.AddDays(-14);
        while (thursday.DayOfWeek != DayOfWeek.Thursday)
            thursday = thursday.AddDays(-1);
        var sunday = thursday.AddDays(3);
        Resolves("rewrite", "week", thursday);

        await Send("redo the weekbook for that week");

        Assert.Equal($"Rewrite|Weekbook|{sunday:yyyy-MM-dd}", _session!.PendingAction);
    }

    /// <summary>
    /// The third book: any day of a finished month resolves to the month's last day, which is the
    /// date the stored Monthbook carries — February's 28th, not the 30th a week-style count would give.
    /// </summary>
    [Fact]
    public async Task A_month_is_dated_by_its_last_day()
    {
        var twoMonthsBack = new DateOnly(Today.Year, Today.Month, 1).AddMonths(-2);
        var midMonth = twoMonthsBack.AddDays(9);
        var monthEnd = new DateOnly(twoMonthsBack.Year, twoMonthsBack.Month, DateTime.DaysInMonth(twoMonthsBack.Year, twoMonthsBack.Month));
        Resolves("rewrite", "month", midMonth);

        var result = await Send("redo that month's monthbook");

        Assert.Contains("Monthbook for", result.Reply, StringComparison.Ordinal);
        Assert.Equal($"Rewrite|Monthbook|{monthEnd:yyyy-MM-dd}", _session!.PendingAction);
    }

    [Fact]
    public async Task Showing_a_monthbook_reads_the_stored_month_back()
    {
        var twoMonthsBack = new DateOnly(Today.Year, Today.Month, 1).AddMonths(-2);
        var monthEnd = new DateOnly(twoMonthsBack.Year, twoMonthsBack.Month, DateTime.DaysInMonth(twoMonthsBack.Year, twoMonthsBack.Month));
        Resolves("show", "month", twoMonthsBack.AddDays(3));
        _digests.GetLatestByDateAsync(_memberId, monthEnd, DigestAudience.Monthbook, Arg.Any<CancellationToken>())
            .Returns(new DigestEntry
            {
                CardiMemberId = _memberId,
                LocalDate = monthEnd,
                Audience = DigestAudience.Monthbook,
                Headline = "A month that held together",
                Text = "Sleep and steps stayed close to Moses's usual across all four weeks.",
            });

        var result = await Send("what did that month's monthbook say");

        Assert.Contains("A month that held together", result.Reply, StringComparison.Ordinal);
        Assert.Contains("all four weeks", result.Reply, StringComparison.Ordinal);
    }

    /// <summary>
    /// Tomorrow rather than today: the service reads its own clock, and a test that crossed UTC
    /// midnight between the fixture's read and the service's would find "today" already finished.
    /// A day in the future is unfinished on either side of midnight.
    /// </summary>
    [Fact]
    public async Task A_period_that_has_not_finished_is_refused_before_any_offer()
    {
        Resolves("rewrite", "day", Today.AddDays(1));

        var result = await Send("rewrite today's daybook");

        Assert.Contains("isn't over yet", result.Reply, StringComparison.Ordinal);
        Assert.Null(_session!.PendingAction);
    }

    [Fact]
    public async Task A_view_only_caregiver_is_told_who_can_change_the_journal()
    {
        Resolves("discard", "day", Reviewed);
        HasDaybook(Reviewed);
        ViewOnlyCaregiver();

        var result = await Send("delete that daybook");

        Assert.Contains("primary caregiver", result.Reply, StringComparison.Ordinal);
        Assert.Null(_session!.PendingAction);
    }

    [Fact]
    public async Task An_unresolved_ask_is_asked_back()
    {
        Resolves(null, null, null);

        var result = await Send("do something with the journal");

        Assert.Contains("which would you like", result.Reply, StringComparison.Ordinal);
    }

    // ── The confirming turn ─────────────────────────────────────────────────

    [Fact]
    public async Task A_yes_carries_out_the_offered_rewrite_and_bills_the_write()
    {
        Resolves("rewrite", "day", Reviewed);
        HasDaybook(Reviewed);
        var written = StoredDaybook(Reviewed, "A quieter day than usual");
        _books.ComposeBookAsync(_memberId, DigestAudience.Daybook, Reviewed, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new JournalRewriteResult(JournalRewriteOutcome.Written, written, new AiUsage { ModelName = "medgemma" }, false));
        await Send("rewrite that day's daybook");
        _router.ClearReceivedCalls();

        var result = await Send("yes");

        Assert.Contains("here's the new Daybook", result.Reply, StringComparison.Ordinal);
        Assert.Contains("A quieter day than usual", result.Reply, StringComparison.Ordinal);
        Assert.True(result.ChangedJournal);
        Assert.Null(_session!.PendingAction);
        await _books.Received(1).ComposeBookAsync(_memberId, DigestAudience.Daybook, Reviewed, Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
        await _digests.Received(1).ReplaceBookAsync(Arg.Is<DigestEntry>(d => d == written), Arg.Any<CancellationToken>());
        await _usages.Received(1).AddAsync(Arg.Is<MemberChatTurnUsage>(u =>
            u.Step == AiCallStep.JournalWrite && u.ProviderSlot == AiProviderSlot.Private));
        await _router.DidNotReceive().RouteAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A book is two calls since the clinical/rewrite split, on two providers, and the ledger
    /// records a row per call. The test above supplies only the private slot's usage, so it would
    /// still pass if the Rewrite-slot row were dropped — which would leave every caregiver-asked
    /// rewrite's Vertex call out of the ledger entirely, and summing the two into one row would
    /// bill a Vertex call as MedGemma.
    /// </summary>
    [Fact]
    public async Task A_yes_bills_both_slots_when_the_book_cost_a_rewrite_call()
    {
        Resolves("rewrite", "day", Reviewed);
        HasDaybook(Reviewed);
        var written = StoredDaybook(Reviewed, "A quieter day than usual");
        _books.ComposeBookAsync(_memberId, DigestAudience.Daybook, Reviewed, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new JournalRewriteResult(
                JournalRewriteOutcome.Written, written, new AiUsage { ModelName = "medgemma" }, false)
            {
                RewriteUsage = new AiUsage { ModelName = "gemini-3.5-flash" },
            });
        await Send("rewrite that day's daybook");

        await Send("yes");

        await _usages.Received(1).AddAsync(Arg.Is<MemberChatTurnUsage>(u =>
            u.Step == AiCallStep.JournalWrite && u.ProviderSlot == AiProviderSlot.Private));
        await _usages.Received(1).AddAsync(Arg.Is<MemberChatTurnUsage>(u =>
            u.Step == AiCallStep.Rewrite && u.ProviderSlot == AiProviderSlot.Rewrite));
    }

    /// <summary>
    /// The model call runs before any transaction; the claim, the store, the turns and the save
    /// then run inside one short transaction the service commits last. The book and the turn that
    /// asked for it land together, and no connection is held across a MedGemma call.
    /// </summary>
    [Fact]
    public async Task A_yes_composes_first_then_claims_stores_and_saves_in_one_transaction()
    {
        Resolves("rewrite", "day", Reviewed);
        HasDaybook(Reviewed);
        _books.ComposeBookAsync(_memberId, DigestAudience.Daybook, Reviewed, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new JournalRewriteResult(JournalRewriteOutcome.Written, StoredDaybook(Reviewed), new AiUsage(), false));
        await Send("rewrite that day's daybook");
        _unitOfWork.ClearReceivedCalls();
        _sessions.ClearReceivedCalls();

        await Send("yes");

        Received.InOrder(() =>
        {
            _books.ComposeBookAsync(_memberId, DigestAudience.Daybook, Reviewed, Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
            _unitOfWork.BeginTransactionAsync();
            _sessions.TryConsumePendingActionAsync(Arg.Any<MemberChatSession>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
            _digests.ReplaceBookAsync(Arg.Any<DigestEntry>(), Arg.Any<CancellationToken>());
            _unitOfWork.SaveChangesAsync();
            _unitOfWork.CommitTransactionAsync();
        });
    }

    /// <summary>
    /// A yes that loses the claim — another request took the offer while the book was composing —
    /// carries nothing out, closes the transaction it opened, tells the caregiver the offer is
    /// gone, and still bills the generation it spent: the ledger records every model call a turn
    /// made, including one whose text was discarded.
    /// </summary>
    [Fact]
    public async Task A_yes_that_loses_the_claim_rolls_back_is_told_and_is_billed()
    {
        Resolves("rewrite", "day", Reviewed);
        HasDaybook(Reviewed);
        _books.ComposeBookAsync(_memberId, DigestAudience.Daybook, Reviewed, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new JournalRewriteResult(JournalRewriteOutcome.Written, StoredDaybook(Reviewed), new AiUsage { ModelName = "medgemma" }, false));
        await Send("rewrite that day's daybook");
        _sessions.TryConsumePendingActionAsync(Arg.Any<MemberChatSession>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns((PendingChatAction?)null);
        _router.ClearReceivedCalls();
        _usages.ClearReceivedCalls();
        _unitOfWork.ClearReceivedCalls();

        var result = await Send("yes");

        Assert.Contains("already been answered or replaced", result.Reply, StringComparison.Ordinal);
        Received.InOrder(() =>
        {
            _unitOfWork.BeginTransactionAsync();
            _unitOfWork.RollbackTransactionAsync();
        });
        await _router.DidNotReceive().RouteAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await _digests.DidNotReceive().ReplaceBookAsync(Arg.Any<DigestEntry>(), Arg.Any<CancellationToken>());
        await _usages.Received(1).AddAsync(Arg.Is<MemberChatTurnUsage>(u => u.Step == AiCallStep.JournalWrite));
    }

    /// <summary>
    /// Demoted while the book was composing: the early check spared nothing here, and the second
    /// check inside the transaction is what keeps the change from landing.
    /// </summary>
    [Fact]
    public async Task Access_lost_during_the_compose_stops_the_change()
    {
        Resolves("rewrite", "day", Reviewed);
        HasDaybook(Reviewed);
        _books.ComposeBookAsync(_memberId, DigestAudience.Daybook, Reviewed, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                // The demotion lands while the model is composing.
                ViewOnlyCaregiver();
                return new JournalRewriteResult(JournalRewriteOutcome.Written, StoredDaybook(Reviewed), new AiUsage(), false);
            });
        await Send("rewrite that day's daybook");

        var result = await Send("yes");

        Assert.Contains("primary caregiver", result.Reply, StringComparison.Ordinal);
        await _digests.DidNotReceive().ReplaceBookAsync(Arg.Any<DigestEntry>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A failure after the write rolls the whole turn back rather than leaving a changed book with
    /// no record of who asked — the offer survives with it, so the same yes can be sent again.
    /// </summary>
    [Fact]
    public async Task A_failure_after_the_write_rolls_the_turn_back()
    {
        Resolves("rewrite", "day", Reviewed);
        HasDaybook(Reviewed);
        _books.ComposeBookAsync(_memberId, DigestAudience.Daybook, Reviewed, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new JournalRewriteResult(JournalRewriteOutcome.Written, StoredDaybook(Reviewed), new AiUsage(), true));
        await Send("rewrite that day's daybook");
        _unitOfWork.ClearReceivedCalls();
        _unitOfWork.SaveChangesAsync().Returns<int>(_ => throw new InvalidOperationException("the save failed"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => Send("yes"));

        await _unitOfWork.Received(1).RollbackTransactionAsync();
        await _unitOfWork.DidNotReceive().CommitTransactionAsync();
    }

    /// <summary>
    /// The offer lands with the reply that shows it: the compare-and-set runs inside the turn's
    /// transaction, and the service commits it after the turns are saved.
    /// </summary>
    [Fact]
    public async Task An_offer_is_written_in_the_same_transaction_as_its_reply()
    {
        Resolves("discard", "day", Reviewed);
        HasDaybook(Reviewed);

        await Send("delete that daybook");

        Received.InOrder(() =>
        {
            _unitOfWork.BeginTransactionAsync();
            _sessions.TryOfferPendingActionAsync(
                Arg.Any<MemberChatSession>(), Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
            _unitOfWork.SaveChangesAsync();
            _unitOfWork.CommitTransactionAsync();
        });
    }

    /// <summary>A no, or an ordinary message, spends the offer without a transaction: nothing is
    /// written that a failure could separate from its confirmation.</summary>
    [Fact]
    public async Task A_no_opens_no_transaction()
    {
        Resolves("discard", "day", Reviewed);
        HasDaybook(Reviewed);
        await Send("delete that daybook");
        _unitOfWork.ClearReceivedCalls();

        await Send("no");

        await _unitOfWork.DidNotReceive().BeginTransactionAsync();
    }

    [Fact]
    public async Task A_yes_carries_out_the_offered_discard()
    {
        Resolves("discard", "day", Reviewed);
        HasDaybook(Reviewed);
        _digests.DeleteBookAsync(_memberId, Reviewed, DigestAudience.Daybook, Arg.Any<CancellationToken>()).Returns(1);
        await Send("delete that daybook");

        var result = await Send("yes please");

        Assert.Contains("has been deleted", result.Reply, StringComparison.Ordinal);
        Assert.True(result.ChangedJournal);
        await _digests.Received(1).DeleteBookAsync(_memberId, Reviewed, DigestAudience.Daybook, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_refused_rewrite_leaves_the_existing_book_and_says_so()
    {
        Resolves("rewrite", "day", Reviewed);
        HasDaybook(Reviewed);
        _books.ComposeBookAsync(_memberId, DigestAudience.Daybook, Reviewed, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new JournalRewriteResult(JournalRewriteOutcome.Discarded, null, new AiUsage(), false));
        await Send("rewrite that day's daybook");

        var result = await Send("yes");

        Assert.Contains("left the existing one in place", result.Reply, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_no_leaves_the_journal_alone_and_spends_the_offer()
    {
        Resolves("discard", "day", Reviewed);
        HasDaybook(Reviewed);
        await Send("delete that daybook");

        var result = await Send("no");

        Assert.Contains("left the journal as it is", result.Reply, StringComparison.Ordinal);
        Assert.Null(_session!.PendingAction);
        await _digests.DidNotReceiveWithAnyArgs().DeleteBookAsync(default, default, default, default);
    }

    /// <summary>
    /// Anything that is not a plain yes or no is a new message: it is routed as itself, and the
    /// offer is gone — a later "yes" to something else must never delete a book.
    /// </summary>
    [Fact]
    public async Task Any_other_message_spends_the_offer_and_is_routed_as_itself()
    {
        Resolves("discard", "day", Reviewed);
        HasDaybook(Reviewed);
        await Send("delete that daybook");
        _router.ClearReceivedCalls();
        Resolves("show", "day", Reviewed);

        await Send("actually, show me that daybook first");

        Assert.Null(_session!.PendingAction);
        await _router.Received(1).RouteAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await _digests.DidNotReceiveWithAnyArgs().DeleteBookAsync(default, default, default, default);
    }

    [Fact]
    public async Task An_expired_offer_is_not_honoured()
    {
        Resolves("discard", "day", Reviewed);
        HasDaybook(Reviewed);
        await Send("delete that daybook");
        _session!.PendingActionExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1);
        _router.ClearReceivedCalls();

        await Send("yes");

        await _digests.DidNotReceiveWithAnyArgs().DeleteBookAsync(default, default, default, default);
        await _router.Received(1).RouteAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Access is checked again at the yes. The offer and the confirmation are two requests, and a
    /// caregiver demoted between them must not carry out what they were offered as primary.
    /// </summary>
    [Fact]
    public async Task Access_is_checked_again_when_the_yes_arrives()
    {
        Resolves("discard", "day", Reviewed);
        HasDaybook(Reviewed);
        await Send("delete that daybook");
        ViewOnlyCaregiver();

        var result = await Send("yes");

        Assert.Contains("primary caregiver", result.Reply, StringComparison.Ordinal);
        await _digests.DidNotReceiveWithAnyArgs().DeleteBookAsync(default, default, default, default);
    }
}
