using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Core.Journal;

namespace CardiTrack.UnitTests.Mobile;

/// <summary>
/// What one save carries. The journal settings PUT replaces every value on the screen, so a draft
/// that drops a field clears it and a draft that resolves one pins it — both silent, and both
/// about a setting the caregiver was not editing.
/// </summary>
public class JournalSettingsDraftTests
{
    /// <summary>A member nobody has tuned: every chosen value null, every effective one defaulted.</summary>
    private static JournalSettingsResponse Untouched() => new()
    {
        DaybookLocalTime = null,
        WeekbookLocalTime = null,
        MonthbookLocalTime = null,
        WeekStartsOn = null,
        BedtimeToleranceMinutes = null,
        WakeToleranceMinutes = null,
        DirectionBoundMinutes = null,
        LevelTolerancePercent = null,

        EffectiveDaybookLocalTime = new TimeOnly(2, 0),
        EffectiveWeekbookLocalTime = new TimeOnly(2, 0),
        EffectiveMonthbookLocalTime = new TimeOnly(2, 0),
        EffectiveWeekStartsOn = DayOfWeek.Monday,
        EffectiveBedtimeToleranceMinutes = 20,
        EffectiveWakeToleranceMinutes = 10,
        EffectiveDirectionBoundMinutes = 360,
        EffectiveLevelTolerancePercent = 0m,
    };

    /// <summary>
    /// The chosen values, not the effective ones. Resending an effective default writes it back as
    /// an explicit choice: the member stops being unset and stops following the default if it ever
    /// moves. The response carries both halves so a client can tell those apart — reading the
    /// effective half would throw the distinction away on the first save of anything.
    /// </summary>
    [Fact]
    public void From_CarriesTheChosenValues_NotWhatTheyResolveTo()
    {
        var request = JournalSettingsDraft.From(Untouched()).ToRequest();

        Assert.Null(request.DaybookLocalTime);
        Assert.Null(request.WeekStartsOn);
        Assert.Null(request.BedtimeToleranceMinutes);
        Assert.Null(request.WakeToleranceMinutes);
        Assert.Null(request.DirectionBoundMinutes);
        Assert.Null(request.LevelTolerancePercent);
    }

    /// <summary>
    /// Editing one field leaves every other one exactly as it was — including the ones still
    /// unset, which stay unset rather than being pinned to today's default.
    /// </summary>
    [Fact]
    public void EditingOneField_LeavesTheRestUnset()
    {
        var request = (JournalSettingsDraft.From(Untouched()) with { DaybookLocalTime = new TimeOnly(7, 30) })
            .ToRequest();

        Assert.Equal(new TimeOnly(7, 30), request.DaybookLocalTime);
        Assert.Null(request.BedtimeToleranceMinutes);
        Assert.Null(request.WeekbookLocalTime);
    }

    /// <summary>
    /// The other half of the same rule: a save about a book timing must not clear a tolerance the
    /// caregiver did choose. Null on this request means "back to the default", so every field the
    /// screen owns rides along on every save.
    /// </summary>
    [Fact]
    public void EditingATiming_KeepsAToleranceTheCaregiverChose()
    {
        var settings = Untouched();
        settings.BedtimeToleranceMinutes = 45;
        settings.EffectiveBedtimeToleranceMinutes = 45;

        var request = (JournalSettingsDraft.From(settings) with { DaybookLocalTime = new TimeOnly(7, 30) })
            .ToRequest();

        Assert.Equal(45, request.BedtimeToleranceMinutes);
    }

    /// <summary>And the reverse: editing a tolerance must not clear a chosen book timing.</summary>
    [Fact]
    public void EditingATolerance_KeepsATimingTheCaregiverChose()
    {
        var settings = Untouched();
        settings.DaybookLocalTime = new TimeOnly(9, 0);
        settings.WeekStartsOn = DayOfWeek.Sunday;

        var request = (JournalSettingsDraft.From(settings) with { BedtimeToleranceMinutes = 30 })
            .ToRequest();

        Assert.Equal(new TimeOnly(9, 0), request.DaybookLocalTime);
        Assert.Equal(DayOfWeek.Sunday, request.WeekStartsOn);
        Assert.Equal(30, request.BedtimeToleranceMinutes);
    }
}
