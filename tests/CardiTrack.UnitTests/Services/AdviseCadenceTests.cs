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
    public void SameDayQuiet_AdvancesAMissedCandidatePastTheWindow()
    {
        var napStart = new TimeOnly(12, 0);
        var napEnd = new TimeOnly(13, 0);
        // last 08:00, 23h/5 = 4h36 waking → 13:36 (the quiet hour is skipped, not waited out).
        Assert.False(AdviseCadence.IsDue(
            UtcAt(12, 30), UtcAt(8), Version, Version, napStart, napEnd, Utc));
        Assert.True(AdviseCadence.IsDue(
            UtcAt(14), UtcAt(8), Version, Version, napStart, napEnd, Utc));
    }

    [Fact]
    public void SameDayQuiet_CountsWritesOnBothSidesOfTheWindowTowardTheCap()
    {
        var napStart = new TimeOnly(12, 0);
        var napEnd = new TimeOnly(13, 0);
        // Waking timeline: 00:00, 04:36, 09:12, 14:48, 19:24 — a sixth would exceed five.
        Assert.True(AdviseCadence.IsDue(
            UtcAt(14, 48), UtcAt(9, 12), Version, Version, napStart, napEnd, Utc));
        Assert.True(AdviseCadence.IsDue(
            UtcAt(19, 24), UtcAt(14, 48), Version, Version, napStart, napEnd, Utc));
        Assert.False(AdviseCadence.IsDue(
            UtcAt(23), UtcAt(19, 24), Version, Version, napStart, napEnd, Utc));
    }

    [Fact]
    public void LongSameDayQuiet_DoesNotSpendTheCapOnTheQuietInterval()
    {
        var quietStart = new TimeOnly(6, 0);
        var quietEnd = new TimeOnly(18, 0);
        // Waking 12h / 5 = 2h24. After 04:48 the next waking offset is 7h12 → 19:12, not a cap.
        Assert.True(AdviseCadence.IsDue(
            UtcAt(19, 12), UtcAt(4, 48), Version, Version, quietStart, quietEnd, Utc));
        Assert.True(AdviseCadence.IsDue(
            UtcAt(21, 36), UtcAt(19, 12), Version, Version, quietStart, quietEnd, Utc));
        Assert.False(AdviseCadence.IsDue(
            UtcAt(23, 59), UtcAt(21, 36), Version, Version, quietStart, quietEnd, Utc));
    }

    [Fact]
    public void FallBackDay_DoesNotAdmitASixthWrite()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        // 18:12 EST after the 2026-11-01 repeated hour; UTC elapsed from midnight is 19h12.
        var last = new DateTime(2026, 11, 1, 23, 12, 0, DateTimeKind.Utc);
        var now = new DateTime(2026, 11, 2, 4, 0, 0, DateTimeKind.Utc);
        Assert.False(AdviseCadence.IsDue(now, last, Version, Version, null, null, zone));
    }

    [Fact]
    public void SpringForwardDay_DoesNotAdmitASixthWrite()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        // 20:00 EDT on 2026-03-08; UTC elapsed from midnight is 19h, wall-clock is 20h.
        var last = new DateTime(2026, 3, 9, 0, 0, 0, DateTimeKind.Utc);
        var now = new DateTime(2026, 3, 9, 3, 48, 0, DateTimeKind.Utc);
        Assert.False(AdviseCadence.IsDue(now, last, Version, Version, null, null, zone));
    }

    [Fact]
    public void SpringForward_NormalizesACandidateThatLandsInTheSkippedHour()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        // Overnight 08:00–00:30: waking is 00:30–08:00, slot 1h30. Last at 00:30, next
        // candidate is 02:00 — which 2026-03-08 never has. Must not throw.
        var quietStart = new TimeOnly(8, 0);
        var quietEnd = new TimeOnly(0, 30);
        var last = new DateTime(2026, 3, 8, 5, 30, 0, DateTimeKind.Utc);
        var now = new DateTime(2026, 3, 8, 8, 0, 0, DateTimeKind.Utc);
        Assert.True(AdviseCadence.IsDue(now, last, Version, Version, quietStart, quietEnd, zone));
    }

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
