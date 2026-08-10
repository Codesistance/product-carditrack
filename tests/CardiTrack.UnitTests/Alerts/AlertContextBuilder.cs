using CardiTrack.Application.Services.Alerts;
using CardiTrack.Domain.Enums;

namespace CardiTrack.UnitTests.Alerts;

/// <summary>
/// Builds <see cref="AlertContext"/> values for rule tests.
/// </summary>
/// <remarks>
/// Defaults describe a healthy, well-observed member: an established baseline, 35 days of normal
/// history, and a normal morning. Each test then changes the one thing it is about, so a rule that
/// starts firing on a healthy member fails every test rather than none — which is the failure mode
/// that matters here.
/// </remarks>
public sealed class AlertContextBuilder
{
    public static readonly DateTime Now = new(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc);

    public const int NormalSteps = 6000;
    public const int NormalRestingHeartRate = 62;
    public const int NormalSleepEfficiency = 88;

    private readonly List<AlertDaySnapshot> _days = [];
    private readonly List<AlertHourSnapshot> _hours = [];
    private readonly List<AlertType> _openTypes = [];

    private AlertSensitivity _sensitivity = AlertSensitivity.Medium;
    private string _timeZoneId = "Europe/London";
    private DateTime? _pausedUntil;
    private bool _hasBaseline = true;
    private int? _avgSleepEfficiency = NormalSleepEfficiency;

    public AlertContextBuilder()
    {
        // 35 whole days of ordinary history, oldest first, ending yesterday.
        for (var offset = 35; offset >= 1; offset--)
            _days.Add(NormalDay(DateOnly.FromDateTime(Now).AddDays(-offset)));

        // A normal morning: movement recorded every hour up to noon.
        for (var hour = 6; hour < 12; hour++)
            _hours.Add(new AlertHourSnapshot { LocalHour = hour, Steps = 300 });
    }

    private static AlertDaySnapshot NormalDay(DateOnly date) => new()
    {
        Date = date,
        Steps = NormalSteps,
        RestingHeartRate = NormalRestingHeartRate,
        SleepEfficiency = NormalSleepEfficiency,
        SleepMinutes = 420,
        ActiveMinutes = 45
    };

    public AlertContextBuilder Sensitivity(AlertSensitivity value) { _sensitivity = value; return this; }
    public AlertContextBuilder TimeZone(string id) { _timeZoneId = id; return this; }
    public AlertContextBuilder PausedUntil(DateTime? until) { _pausedUntil = until; return this; }
    public AlertContextBuilder NoBaseline() { _hasBaseline = false; return this; }
    public AlertContextBuilder NoSleepBaseline() { _avgSleepEfficiency = null; return this; }
    public AlertContextBuilder AlreadyOpen(AlertType type) { _openTypes.Add(type); return this; }

    /// <summary>Overwrites the most recent <paramref name="count"/> days.</summary>
    public AlertContextBuilder LastDays(int count, Func<AlertDaySnapshot, AlertDaySnapshot> change)
    {
        for (var i = _days.Count - count; i < _days.Count; i++)
            _days[i] = change(_days[i]);
        return this;
    }

    /// <summary>Drops the most recent <paramref name="count"/> days entirely — a watch on charge.</summary>
    public AlertContextBuilder MissingLastDays(int count)
    {
        _days.RemoveRange(_days.Count - count, count);
        return this;
    }

    public AlertContextBuilder NoHourlyData() { _hours.Clear(); return this; }

    public AlertContextBuilder MorningHours(int hoursReported, int stepsPerHour)
    {
        _hours.Clear();
        for (var i = 0; i < hoursReported; i++)
            _hours.Add(new AlertHourSnapshot { LocalHour = 6 + i, Steps = stepsPerHour });
        return this;
    }

    public AlertContext Build() => new()
    {
        UtcNow = Now,
        CardiMemberId = Guid.Parse("55555555-5555-5555-5555-555555555555"),
        Sensitivity = _sensitivity,
        TimeZoneId = _timeZoneId,
        MonitoringPausedUntil = _pausedUntil,
        Baseline = _hasBaseline
            ? new AlertBaselineSnapshot
            {
                PeriodDays = 30,
                AvgSteps = NormalSteps,
                StdDevSteps = 900,
                AvgRestingHeartRate = NormalRestingHeartRate,
                StdDevHeartRate = 3,
                AvgSleepMinutes = 420,
                AvgSleepEfficiency = _avgSleepEfficiency
            }
            : null,
        RecentDays = _days,
        TodayHours = _hours,
        OpenAlertTypes = _openTypes
    };
}
