using CardiTrack.Application.Services;
using CardiTrack.Application.Services.Notifications;

namespace CardiTrack.UnitTests.Services;

public class QuietHoursTests
{
    private static readonly TimeOnly TenPm = new(22, 0);
    private static readonly TimeOnly SevenAm = new(7, 0);

    [Theory]
    [InlineData(22, 0, true)]
    [InlineData(23, 30, true)]
    [InlineData(0, 0, true)]
    [InlineData(6, 59, true)]
    [InlineData(7, 0, false)]
    [InlineData(12, 0, false)]
    [InlineData(21, 59, false)]
    public void OvernightWindow_WrapsPastMidnight(int hour, int minute, bool within) =>
        Assert.Equal(within, QuietHours.Contains(TenPm, SevenAm, new TimeOnly(hour, minute)));

    [Theory]
    [InlineData(12, 0, true)]
    [InlineData(12, 59, true)]
    [InlineData(13, 0, false)]
    [InlineData(11, 59, false)]
    public void SameDayWindow_DoesNotWrap(int hour, int minute, bool within) =>
        Assert.Equal(within, QuietHours.Contains(new TimeOnly(12, 0), new TimeOnly(13, 0), new TimeOnly(hour, minute)));

    [Fact]
    public void OvernightDuration_IsNineHours() =>
        Assert.Equal(TimeSpan.FromHours(9), QuietHours.Duration(TenPm, SevenAm));

    [Fact]
    public void SameDayDuration_IsOneHour() =>
        Assert.Equal(TimeSpan.FromHours(1), QuietHours.Duration(new TimeOnly(12, 0), new TimeOnly(13, 0)));
}

public class AdviseCadenceTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly TimeOnly QuietStart = new(22, 0);
    private static readonly TimeOnly QuietEnd = new(7, 0);
    private const int Version = 8;

    private static DateTime UtcAt(int hour, int minute = 0, int day = 18) =>
        new(2026, 9, day, hour, minute, 0, DateTimeKind.Utc);

    [Fact]
    public void NoPriorRow_OutsideQuietHours_IsDue() =>
        Assert.True(AdviseCadence.IsDue(UtcAt(12), null, Version, Version, QuietStart, QuietEnd, Utc));

    [Fact]
    public void NoPriorRow_InsideQuietHours_IsNotDue() =>
        Assert.False(AdviseCadence.IsDue(UtcAt(3), null, Version, Version, QuietStart, QuietEnd, Utc));

    [Fact]
    public void FirstWakingPassOfTheLocalDay_IsDue() =>
        Assert.True(AdviseCadence.IsDue(
            UtcAt(7), UtcAt(19, day: 17), Version, Version, QuietStart, QuietEnd, Utc));

    [Fact]
    public void StillQuiet_EvenWhenYesterdaysRowIsOld_IsNotDue() =>
        Assert.False(AdviseCadence.IsDue(
            UtcAt(6), UtcAt(19, day: 17), Version, Version, QuietStart, QuietEnd, Utc));

    [Fact]
    public void WithinTheWakingSlot_IsNotDue() =>
        Assert.False(AdviseCadence.IsDue(
            UtcAt(9), UtcAt(7), Version, Version, QuietStart, QuietEnd, Utc));

    [Fact]
    public void AfterTheWakingSlot_IsDue() =>
        Assert.True(AdviseCadence.IsDue(
            UtcAt(10), UtcAt(7), Version, Version, QuietStart, QuietEnd, Utc));

    [Fact]
    public void FifthSlot_IsTheLastOfTheWakingDay()
    {
        // 07, 10, 13, 16, 19 — waking 07–22 is 15h / 5 = 3h.
        Assert.True(AdviseCadence.IsDue(
            UtcAt(19), UtcAt(16), Version, Version, QuietStart, QuietEnd, Utc));
        Assert.False(AdviseCadence.IsDue(
            UtcAt(19, 1), UtcAt(19), Version, Version, QuietStart, QuietEnd, Utc));
        Assert.False(AdviseCadence.IsDue(
            UtcAt(21), UtcAt(19), Version, Version, QuietStart, QuietEnd, Utc));
    }

    [Fact]
    public void NextSlotLandingInQuietHours_WaitsUntilTomorrow() =>
        Assert.False(AdviseCadence.IsDue(
            UtcAt(22), UtcAt(19), Version, Version, QuietStart, QuietEnd, Utc));

    [Fact]
    public void OlderPromptVersion_WaitsOutQuietHours()
    {
        Assert.False(AdviseCadence.IsDue(
            UtcAt(3), UtcAt(2), storedPromptVersion: 7, Version, QuietStart, QuietEnd, Utc));
        Assert.True(AdviseCadence.IsDue(
            UtcAt(7), UtcAt(2), storedPromptVersion: 7, Version, QuietStart, QuietEnd, Utc));
    }

    [Fact]
    public void NoQuietHours_SpacesFiveAcrossTheUtcDay()
    {
        // 24h / 5 = 4.8h.
        Assert.False(AdviseCadence.IsDue(
            UtcAt(4), UtcAt(0), Version, Version, null, null, Utc));
        Assert.True(AdviseCadence.IsDue(
            UtcAt(5), UtcAt(0), Version, Version, null, null, Utc));
    }

    [Fact]
    public void NoQuietHours_AllowsANightPass() =>
        Assert.True(AdviseCadence.IsDue(UtcAt(3), null, Version, Version, null, null, Utc));

    [Fact]
    public void ANewLocalDay_IsDueEvenWhenTheRollingSlotHasNotElapsed() =>
        Assert.True(AdviseCadence.IsDue(
            UtcAt(0, 30, day: 18), UtcAt(23, day: 17), Version, Version, null, null, Utc));

    [Fact]
    public void QuietHoursAreReadOnTheAnchorClock_NotUtc()
    {
        var london = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
        // 21:30 UTC is 22:30 BST — inside 22:00–07:00 London quiet hours, still waking on UTC.
        var bstNight = new DateTime(2026, 9, 18, 21, 30, 0, DateTimeKind.Utc);
        var last = new DateTime(2026, 9, 18, 12, 0, 0, DateTimeKind.Utc);
        Assert.False(AdviseCadence.IsDue(
            bstNight, last, Version, Version, QuietStart, QuietEnd, london));
        Assert.True(AdviseCadence.IsDue(
            bstNight, last, Version, Version, QuietStart, QuietEnd, Utc));
    }
}
