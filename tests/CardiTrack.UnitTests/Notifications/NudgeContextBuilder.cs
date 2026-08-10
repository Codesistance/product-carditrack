using CardiTrack.Application.Services.Notifications;
using CardiTrack.Domain.Enums;

namespace CardiTrack.UnitTests.Notifications;

/// <summary>
/// Builds <see cref="NudgeContext"/> values for rule tests.
/// </summary>
/// <remarks>
/// Defaults describe a healthy, established account — a member with a connected device syncing an
/// hour ago and a baseline in place — so each test states only the one thing it is about, and a
/// rule that starts firing on a healthy account fails every test rather than none.
/// </remarks>
public sealed class NudgeContextBuilder
{
    public static readonly DateTime Now = new(2026, 8, 10, 6, 0, 0, DateTimeKind.Utc);

    private readonly Guid _userId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private readonly Guid _memberId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private string _timeZoneId = "Europe/London";
    private DateTime _userCreated = Now.AddDays(-90);
    private bool _includeMember = true;
    private bool _hasMedicalNotes = true;
    private DateTime? _pausedUntil;
    private bool _everHadConnection = true;
    private int _daysCaptured = 30;
    private DateOnly? _lastActivity = DateOnly.FromDateTime(Now);
    private bool _hasBaseline = true;
    private bool _redAlert;
    private bool _deviceSilenceAlert;
    private bool _isOwner = true;
    private readonly List<NudgeConnectionSnapshot> _connections = [];
    private readonly List<NudgeMuteSnapshot> _mutes = [];

    public NudgeContextBuilder()
    {
        _connections.Add(Connection());
    }

    public static NudgeConnectionSnapshot Connection(
        ConnectionStatus status = ConnectionStatus.Connected,
        DateTime? lastSync = null,
        params string[] scopes) => new()
        {
            Id = Guid.Parse("33333333-3333-3333-3333-333333333333"),
            DeviceType = DeviceType.Fitbit,
            Status = status,
            LastSyncDate = lastSync ?? Now.AddHours(-1),
            Scopes = scopes.Length > 0
                ? scopes
                : ["activity_and_fitness", "health_metrics_and_measurements", "sleep"]
        };

    public NudgeContextBuilder AccountLevel() { _includeMember = false; return this; }
    public NudgeContextBuilder TimeZone(string id) { _timeZoneId = id; return this; }
    public NudgeContextBuilder UserCreated(DateTime at) { _userCreated = at; return this; }
    public NudgeContextBuilder NoMedicalNotes() { _hasMedicalNotes = false; return this; }
    public NudgeContextBuilder PausedUntil(DateTime? until) { _pausedUntil = until; return this; }
    public NudgeContextBuilder NeverHadConnection() { _everHadConnection = false; return this; }
    public NudgeContextBuilder DaysCaptured(int days) { _daysCaptured = days; return this; }
    public NudgeContextBuilder LastActivity(DateOnly? date) { _lastActivity = date; return this; }
    public NudgeContextBuilder NoBaseline() { _hasBaseline = false; return this; }
    public NudgeContextBuilder WithRedAlert() { _redAlert = true; return this; }
    public NudgeContextBuilder WithDeviceSilenceAlert() { _deviceSilenceAlert = true; return this; }
    public NudgeContextBuilder NotOwner() { _isOwner = false; return this; }

    public NudgeContextBuilder NoConnections()
    {
        _connections.Clear();
        return this;
    }

    public NudgeContextBuilder WithConnections(params NudgeConnectionSnapshot[] connections)
    {
        _connections.Clear();
        _connections.AddRange(connections);
        return this;
    }

    public NudgeContextBuilder Muting(
        string? ruleCode = null, NotificationCategory? category = null,
        Guid? memberId = null, DateTime? until = null)
    {
        _mutes.Add(new NudgeMuteSnapshot
        {
            RuleCode = ruleCode,
            Category = category,
            CardiMemberId = memberId,
            MutedUntil = until
        });
        return this;
    }

    public NudgeContext Build() => new()
    {
        UtcNow = Now,
        OrganizationId = Guid.Parse("44444444-4444-4444-4444-444444444444"),
        IsOwner = _isOwner,
        User = new NudgeUserSnapshot
        {
            Id = _userId,
            TimeZoneId = _timeZoneId,
            Locale = "en-GB",
            CreatedDate = _userCreated
        },
        Member = _includeMember
            ? new NudgeMemberSnapshot
            {
                Id = _memberId,
                CreatedDate = Now.AddDays(-90),
                HasMedicalNotes = _hasMedicalNotes,
                MonitoringPausedUntil = _pausedUntil,
                EverHadConnection = _everHadConnection,
                DaysCaptured = _daysCaptured,
                LastActivityDate = _lastActivity,
                HasEstablishedBaseline = _hasBaseline,
                HasUnacknowledgedRedAlert = _redAlert,
                HasOpenDeviceSilenceAlert = _deviceSilenceAlert
            }
            : null,
        Connections = _includeMember ? _connections : [],
        Mutes = _mutes
    };
}
