using System.Linq.Expressions;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Services.Notifications;
using CardiTrack.Application.Services.Notifications.Rules;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using NSubstitute;

namespace CardiTrack.UnitTests.Notifications;

/// <summary>
/// <c>MemberSetup</c> on the notification summary: which members it covers, what a relative sees,
/// and that it is computed from the member's data rather than from the inbox rows.
/// </summary>
public class NotificationSummaryMemberSetupTests
{
    private static readonly Guid Caller = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Pop = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Nan = Guid.Parse("77777777-7777-7777-7777-777777777777");

    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly INotificationSnapshotQueries _snapshots = Substitute.For<INotificationSnapshotQueries>();
    private readonly List<CardiMember> _members =
    [
        new() { Id = Pop, FirstName = "Pop", LastName = "Example" },
        new() { Id = Nan, FirstName = "Nan", LastName = "Example" }
    ];

    public NotificationSummaryMemberSetupTests()
    {
        _unitOfWork.Notifications
            .GetTopForDashboardAsync(Caller, Arg.Any<int>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Notification>());

        _unitOfWork.CardiMembers
            .FindAsync(Arg.Any<Expression<Func<CardiMember, bool>>>())
            .Returns(call =>
            {
                var predicate = call.Arg<Expression<Func<CardiMember, bool>>>().Compile();
                return _members.Where(predicate);
            });
    }

    private NotificationService CreateSut() =>
        new(_unitOfWork, new NoOpNotificationGapResolver(), _snapshots);

    private void ContextsAre(params NudgeContext[] contexts) =>
        _snapshots.BuildContextsForUserAsync(Caller, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(contexts);

    private static NudgeContext ForMember(Guid memberId, NudgeContextBuilder builder) =>
        builder.Build() with
        {
            Member = builder.Build().Member! with { Id = memberId }
        };

    [Fact]
    public async Task CoversExactlyTheMembersTheCallerHasAContextFor_ByFirstName()
    {
        ContextsAre(
            new NudgeContextBuilder().AccountLevel().Build(),
            ForMember(Pop, new NudgeContextBuilder()),
            ForMember(Nan, new NudgeContextBuilder()));

        var summary = await CreateSut().GetSummaryAsync(Caller);

        Assert.Equal(new[] { "Nan", "Pop" }, summary.MemberSetup.Select(m => m.CardiMemberFirstName));
        Assert.Equal(new[] { Nan, Pop }, summary.MemberSetup.Select(m => m.CardiMemberId));
    }

    [Fact]
    public async Task AMemberTheCallerHasNoContextFor_IsNotIncluded()
    {
        // Nan exists, but the snapshot (the caller's links) only reaches Pop.
        ContextsAre(ForMember(Pop, new NudgeContextBuilder()));

        var summary = await CreateSut().GetSummaryAsync(Caller);

        Assert.Equal(Pop, Assert.Single(summary.MemberSetup).CardiMemberId);
    }

    [Fact]
    public async Task ACallerWatchingNobody_GetsAnEmptyChecklist_AndTheRestOfTheSummary()
    {
        ContextsAre(new NudgeContextBuilder().AccountLevel().Build());
        _unitOfWork.Notifications.CountUnseenAsync(Caller, Arg.Any<CancellationToken>()).Returns(3);

        var summary = await CreateSut().GetSummaryAsync(Caller);

        Assert.Empty(summary.MemberSetup);
        Assert.Equal(3, summary.UnseenCount);
    }

    [Fact]
    public async Task CountsAndStepsAreProjectedInOrder()
    {
        ContextsAre(ForMember(Pop, new NudgeContextBuilder().NoEmergencyContact().NoConnections()));

        var pop = Assert.Single((await CreateSut().GetSummaryAsync(Caller)).MemberSetup);

        Assert.Equal(3, pop.Total);
        Assert.Equal(2, pop.Done);
        Assert.Equal(
            new[] { "emergency-contact", "time-zone", "medical-information" },
            pop.Steps.Select(s => s.Key));

        var next = pop.Steps.First(s => !s.Done);
        Assert.Equal("emergency-contact", next.Key);
        Assert.Equal("Emergency contact", next.Title);
        Assert.Equal($"carditrack://cardimembers/{Pop}/edit#emergencyContact", next.ActionDeepLink);
    }

    /// <summary>
    /// A relative is shown the same progress — it is a fact about the member — but flagged as not
    /// the one being asked, the way their copy of a nudge is.
    /// </summary>
    [Fact]
    public async Task ARelativeSeesTheSameProgress_FlaggedAsNotTheOwner()
    {
        ContextsAre(
            ForMember(Pop, new NudgeContextBuilder().NoEmergencyContact()),
            ForMember(Nan, new NudgeContextBuilder().NoEmergencyContact().NotOwner()));

        var summary = await CreateSut().GetSummaryAsync(Caller);
        var pop = summary.MemberSetup.Single(m => m.CardiMemberId == Pop);
        var nan = summary.MemberSetup.Single(m => m.CardiMemberId == Nan);

        Assert.True(pop.IsOwner);
        Assert.False(nan.IsOwner);
        Assert.Equal((pop.Done, pop.Total), (nan.Done, nan.Total));
    }

    /// <summary>
    /// Snoozing is "not now", and it changes nothing about the member — so the step stays open while
    /// the snoozed row sits out of the dashboard cards.
    /// </summary>
    [Fact]
    public async Task ASnoozedNudge_LeavesItsStepNotDone()
    {
        var notificationId = Guid.NewGuid();
        var row = new Notification
        {
            Id = notificationId,
            UserId = Caller,
            CardiMemberId = Pop,
            RuleCode = EmergencyContactMissingRule.Code,
            RuleVersion = 1,
            Category = NotificationCategory.Unlock,
            Priority = NotificationPriority.High,
            Fingerprint = "pop-contact",
            ActionDeepLink = $"carditrack://cardimembers/{Pop}/edit#emergencyContact",
            State = NotificationState.Open,
            IsOwner = true
        };
        _unitOfWork.Notifications.GetByIdAsync(notificationId).Returns(row);
        ContextsAre(ForMember(Pop, new NudgeContextBuilder().NoEmergencyContact()));

        var sut = CreateSut();
        await sut.SnoozeAsync(Caller, notificationId, TimeSpan.FromDays(7));
        var summary = await sut.GetSummaryAsync(Caller);

        Assert.Equal(NotificationState.Snoozed, row.State);
        Assert.Empty(summary.DashboardCards);
        Assert.False(Assert.Single(summary.MemberSetup).Steps.Single(s => s.Key == "emergency-contact").Done);
    }

    [Fact]
    public async Task TheCallersOwnMute_TakesTheStepOffTheirTotal()
    {
        ContextsAre(ForMember(Pop, new NudgeContextBuilder()
            .NoEmergencyContact()
            .Muting(ruleCode: EmergencyContactMissingRule.Code, memberId: Pop)));

        var pop = Assert.Single((await CreateSut().GetSummaryAsync(Caller)).MemberSetup);

        Assert.DoesNotContain(pop.Steps, s => s.Key == "emergency-contact");
        Assert.Equal(pop.Steps.Count, pop.Total);
    }

    [Fact]
    public async Task TheSnapshotIsBuiltForTheCaller()
    {
        ContextsAre();

        await CreateSut().GetSummaryAsync(Caller);

        await _snapshots.Received(1).BuildContextsForUserAsync(Caller, Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }
}
