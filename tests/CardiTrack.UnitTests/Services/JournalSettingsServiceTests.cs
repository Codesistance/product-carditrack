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
        DayOfWeek? weekStartsOn = null,
        int? bedtimeTolerance = null,
        int? wakeTolerance = null,
        int? directionBound = null,
        decimal? levelTolerance = null)
        => new()
        {
            DaybookLocalTime = daybook,
            WeekbookLocalTime = weekbook,
            MonthbookLocalTime = monthbook,
            WeekStartsOn = weekStartsOn,
            BedtimeToleranceMinutes = bedtimeTolerance,
            WakeToleranceMinutes = wakeTolerance,
            DirectionBoundMinutes = directionBound,
            LevelTolerancePercent = levelTolerance,
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

    /// <summary>
    /// A book whose generator does not exist must not imply it is running, and one whose
    /// generator does exist must not be shown as coming. All three now exist, so every timing on
    /// the settings screen governs something that actually runs.
    /// </summary>
    /// <remarks>
    /// These flags were <c>false</c> while the generators were unbuilt, and this test failing on
    /// the day one landed is the point of it: the app words a row differently depending on them,
    /// so a stale <c>false</c> would tell a caregiver their Monthbook is still coming after it had
    /// started arriving.
    /// </remarks>
    [Fact]
    public async Task Each_book_reports_whether_anything_actually_writes_it()
    {
        var settings = await CreateService().GetAsync(_userId, _memberId);

        Assert.True(settings.WeekbookAvailable);
        Assert.True(settings.MonthbookAvailable);
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

    // ── Comparison tolerances ───────────────────────────────────────────────────

    /// <summary>
    /// A member nobody has tuned reads exactly as they did before the setting existed — the same
    /// stance the timings take, and what makes the four columns need no backfill.
    /// </summary>
    [Fact]
    public async Task An_untuned_member_reports_the_default_tolerances_as_effective()
    {
        var settings = await CreateService().GetAsync(_userId, _memberId);

        Assert.Null(settings.BedtimeToleranceMinutes);
        Assert.Null(settings.WakeToleranceMinutes);

        Assert.Equal(20, settings.EffectiveBedtimeToleranceMinutes);
        Assert.Equal(10, settings.EffectiveWakeToleranceMinutes);
        Assert.Equal(360, settings.EffectiveDirectionBoundMinutes);
        Assert.Equal(0m, settings.EffectiveLevelTolerancePercent);
    }

    /// <summary>
    /// The bounds ride in the response for the same reason the time window does: a client that
    /// has to guess them can offer a setting the books would refuse.
    /// </summary>
    [Fact]
    public async Task The_tolerance_bounds_are_published_so_a_client_cannot_offer_an_unreachable_one()
    {
        var settings = await CreateService().GetAsync(_userId, _memberId);

        Assert.Equal(120, settings.MaximumToleranceMinutes);
        Assert.Equal(60, settings.MinimumDirectionBoundMinutes);
        Assert.Equal(720, settings.MaximumDirectionBoundMinutes);
        Assert.Equal(25m, settings.MaximumLevelTolerancePercent);
    }

    /// <summary>
    /// The rungs a client offers ride down with the bounds, so an app cannot invent its own ladder
    /// and drift from the server the day either changes — the same stance the book timings take on
    /// their window and step.
    /// </summary>
    [Fact]
    public async Task The_offerable_rungs_are_published_alongside_the_bounds()
    {
        var settings = await CreateService().GetAsync(_userId, _memberId);

        Assert.Contains(20, settings.SelectableToleranceMinutes);
        Assert.Contains(360, settings.SelectableDirectionBoundMinutes);
        Assert.Contains(0m, settings.SelectableLevelTolerancePercents);

        // Offerable, not enforced: every rung has to be a value validation would in fact accept.
        Assert.All(settings.SelectableToleranceMinutes, m => Assert.InRange(m, 0, settings.MaximumToleranceMinutes));
        Assert.All(
            settings.SelectableDirectionBoundMinutes,
            m => Assert.InRange(m, settings.MinimumDirectionBoundMinutes, settings.MaximumDirectionBoundMinutes));
    }

    [Fact]
    public async Task Chosen_tolerances_are_stored_and_reported_as_effective()
    {
        var settings = await CreateService().UpdateAsync(
            _userId,
            _memberId,
            Request(bedtimeTolerance: 45, wakeTolerance: 5, directionBound: 240, levelTolerance: 2.5m));

        Assert.Equal(45, _member.DaybookBedtimeToleranceMinutes);
        Assert.Equal(5, _member.DaybookWakeToleranceMinutes);
        Assert.Equal(240, _member.DaybookDirectionBoundMinutes);
        Assert.Equal(2.5m, _member.DaybookLevelTolerancePercent);

        Assert.Equal(45, settings.EffectiveBedtimeToleranceMinutes);
        Assert.Equal(2.5m, settings.EffectiveLevelTolerancePercent);
        await _unitOfWork.Received(1).SaveChangesAsync();
    }

    [Fact]
    public async Task A_null_tolerance_clears_the_choice_back_to_the_default()
    {
        _member.DaybookBedtimeToleranceMinutes = 45;

        var settings = await CreateService().UpdateAsync(_userId, _memberId, Request());

        Assert.Null(_member.DaybookBedtimeToleranceMinutes);
        Assert.Equal(20, settings.EffectiveBedtimeToleranceMinutes);
    }

    /// <summary>
    /// Zero is a real choice, not an absent one: a caregiver who wants every minute of drift named
    /// is asking for the format's own resolution and nothing wider.
    /// </summary>
    [Fact]
    public async Task A_zero_tolerance_is_a_choice_rather_than_a_clear()
    {
        await CreateService().UpdateAsync(_userId, _memberId, Request(bedtimeTolerance: 0));

        Assert.Equal(0, _member.DaybookBedtimeToleranceMinutes);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(121)]
    public async Task An_out_of_range_tolerance_is_refused_and_nothing_is_saved(int minutes)
    {
        var service = CreateService();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.UpdateAsync(_userId, _memberId, Request(bedtimeTolerance: minutes)));

        Assert.Null(_member.DaybookBedtimeToleranceMinutes);
        await _unitOfWork.DidNotReceive().SaveChangesAsync();
    }

    /// <summary>
    /// Past half a day, earlier and later stop being different answers — a bound above it could
    /// never be reached, so it is refused rather than stored as a setting that does nothing.
    /// </summary>
    [Theory]
    [InlineData(30)]
    [InlineData(721)]
    public async Task A_direction_bound_the_clock_cannot_reach_is_refused(int minutes)
    {
        var service = CreateService();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.UpdateAsync(_userId, _memberId, Request(directionBound: minutes)));

        await _unitOfWork.DidNotReceive().SaveChangesAsync();
    }

    [Theory]
    [InlineData(-0.5)]
    [InlineData(25.1)]
    public async Task An_out_of_range_level_band_is_refused(decimal percent)
    {
        var service = CreateService();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.UpdateAsync(_userId, _memberId, Request(levelTolerance: percent)));

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
