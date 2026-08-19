using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// Pins who may move a CardiJournal timing, what a null field means, and that a time the
/// generator could not honour never reaches the database.
/// </summary>
public class JournalSettingsServiceTests
{
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly ICardiMemberRepository _members = Substitute.For<ICardiMemberRepository>();
    private readonly IUserCardiMemberRepository _links = Substitute.For<IUserCardiMemberRepository>();
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly ICardiMemberAccessService _access = Substitute.For<ICardiMemberAccessService>();

    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _memberId = Guid.NewGuid();
    private readonly CardiMember _member;

    public JournalSettingsServiceTests()
    {
        _member = new CardiMember { Id = _memberId, Name = "Ada" };

        _unitOfWork.CardiMembers.Returns(_members);
        _unitOfWork.UserCardiMembers.Returns(_links);
        _unitOfWork.Users.Returns(_users);
        _members.GetByIdAsync(_memberId).Returns(_member);
        _links.GetByCardiMemberIdAsync(_memberId).Returns(new List<UserCardiMember>());
    }

    private JournalSettingsService CreateService() => new(_unitOfWork, _access);

    private static UpdateJournalSettingsRequest Request(
        TimeOnly? daybook = null,
        TimeOnly? weekbook = null,
        TimeOnly? monthbook = null,
        DayOfWeek? weekStartsOn = null)
        => new()
        {
            DaybookLocalTime = daybook,
            WeekbookLocalTime = weekbook,
            MonthbookLocalTime = monthbook,
            WeekStartsOn = weekStartsOn,
        };

    [Fact]
    public async Task An_unset_member_reports_the_defaults_as_effective()
    {
        var settings = await CreateService().GetAsync(_userId, _memberId);

        Assert.Null(settings.DaybookLocalTime);
        Assert.Equal(new TimeOnly(2, 0), settings.EffectiveDaybookLocalTime);
        Assert.Equal(DayOfWeek.Monday, settings.EffectiveWeekStartsOn);
    }

    [Fact]
    public async Task The_window_and_step_are_published_so_a_client_cannot_offer_an_unhonourable_time()
    {
        var settings = await CreateService().GetAsync(_userId, _memberId);

        Assert.Equal(new TimeOnly(1, 0), settings.EarliestSelectableTime);
        Assert.Equal(new TimeOnly(12, 0), settings.LatestSelectableTime);
        Assert.Equal(30, settings.StepMinutes);
    }

    /// <summary>The generators are R2; the setting must not imply they are running.</summary>
    [Fact]
    public async Task The_unbuilt_books_report_themselves_unavailable()
    {
        var settings = await CreateService().GetAsync(_userId, _memberId);

        Assert.False(settings.WeekbookAvailable);
        Assert.False(settings.MonthbookAvailable);
    }

    [Fact]
    public async Task A_chosen_time_is_stored_and_saved()
    {
        var settings = await CreateService()
            .UpdateAsync(_userId, _memberId, Request(daybook: new TimeOnly(7, 30)));

        Assert.Equal(new TimeOnly(7, 30), _member.DaybookLocalTime);
        Assert.Equal(new TimeOnly(7, 30), settings.EffectiveDaybookLocalTime);
        await _unitOfWork.Received(1).SaveChangesAsync();
    }

    [Fact]
    public async Task A_null_field_clears_the_choice_back_to_the_default()
    {
        _member.DaybookLocalTime = new TimeOnly(9, 0);

        var settings = await CreateService().UpdateAsync(_userId, _memberId, Request(daybook: null));

        Assert.Null(_member.DaybookLocalTime);
        Assert.Equal(new TimeOnly(2, 0), settings.EffectiveDaybookLocalTime);
    }

    [Fact]
    public async Task The_week_start_is_stored()
    {
        await CreateService()
            .UpdateAsync(_userId, _memberId, Request(weekStartsOn: DayOfWeek.Sunday));

        Assert.Equal(DayOfWeek.Sunday, _member.JournalWeekStartsOn);
    }

    [Theory]
    [InlineData(0, 0)]   // midnight — the day's tail has not landed
    [InlineData(23, 30)] // long past useful
    [InlineData(2, 17)]  // off the half-hour the job actually runs on
    public async Task An_unhonourable_time_is_refused_and_nothing_is_saved(int hour, int minute)
    {
        var service = CreateService();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.UpdateAsync(_userId, _memberId, Request(daybook: new TimeOnly(hour, minute))));

        Assert.Null(_member.DaybookLocalTime);
        await _unitOfWork.DidNotReceive().SaveChangesAsync();
    }

    /// <summary>
    /// Validation runs before the member is loaded or touched, so a bad Weekbook time cannot
    /// leave a good Daybook time half-applied.
    /// </summary>
    [Fact]
    public async Task One_bad_field_rejects_the_whole_request()
    {
        var service = CreateService();

        await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateAsync(
            _userId, _memberId, Request(daybook: new TimeOnly(6, 0), weekbook: new TimeOnly(22, 0))));

        Assert.Null(_member.DaybookLocalTime);
        await _unitOfWork.DidNotReceive().SaveChangesAsync();
    }

    /// <summary>
    /// A book is written once for the member, so moving its time changes what every other
    /// caregiver receives — the same bar as pausing monitoring, not merely reading.
    /// </summary>
    [Fact]
    public async Task Changing_a_timing_needs_manage_access()
    {
        _access.RequireManageAccessAsync(_userId, _memberId, Arg.Any<CancellationToken>())
            .ThrowsAsync(new KeyNotFoundException("CardiMember not found"));

        var service = CreateService();

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            service.UpdateAsync(_userId, _memberId, Request(daybook: new TimeOnly(7, 0))));

        await _unitOfWork.DidNotReceive().SaveChangesAsync();
    }

    [Fact]
    public async Task Reading_the_timings_needs_view_access()
    {
        _access.RequireViewAccessAsync(_userId, _memberId, Arg.Any<CancellationToken>())
            .ThrowsAsync(new KeyNotFoundException("CardiMember not found"));

        var service = CreateService();

        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.GetAsync(_userId, _memberId));
    }
}
