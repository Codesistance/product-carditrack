using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Services.Notifications;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Services;
using CardiTrack.Infrastructure.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// Pins the device-silence rules: silence means no granular readings (not "no sync"), it only
/// counts when the whole silent window sits inside the member's local waking hours, and a dead
/// device produces exactly one yellow alert until a caregiver resolves it.
/// </summary>
public class InactivityDetectionServiceTests
{
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly ICardiMemberRepository _members = Substitute.For<ICardiMemberRepository>();
    private readonly IUserCardiMemberRepository _links = Substitute.For<IUserCardiMemberRepository>();
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly IGranularMetricRepository _granular = Substitute.For<IGranularMetricRepository>();
    private readonly IAlertRepository _alerts = Substitute.For<IAlertRepository>();
    private readonly IAlertPreferenceRepository _alertPreferences = Substitute.For<IAlertPreferenceRepository>();
    private readonly IDeviceConnectionRepository _connections = Substitute.For<IDeviceConnectionRepository>();
    private readonly IDeviceSyncService _deviceSync = Substitute.For<IDeviceSyncService>();

    private readonly Guid _memberId = Guid.NewGuid();
    private readonly Guid _userId = Guid.NewGuid();

    /// <summary>13:40 UTC — mid-afternoon in London (BST 14:40), well inside waking hours.</summary>
    private static readonly DateTime UtcNow = new(2026, 8, 10, 13, 40, 0, DateTimeKind.Utc);

    private static readonly InactivityDetectionRules Rules = new()
    {
        SilenceThresholdMinutes = 120,
        WakingStartHour = 7,
        WakingEndHour = 22,
    };

    public InactivityDetectionServiceTests()
    {
        _unitOfWork.CardiMembers.Returns(_members);
        _unitOfWork.UserCardiMembers.Returns(_links);
        _unitOfWork.Users.Returns(_users);
        _unitOfWork.AlertPreferences.Returns(_alertPreferences);
        _unitOfWork.GranularMetrics.Returns(_granular);
        _unitOfWork.Alerts.Returns(_alerts);
        _unitOfWork.DeviceConnections.Returns(_connections);
        // No device by default: the pre-alert probe has nothing to pull, so detection behaves
        // exactly as it did before the probe existed.
        _connections.GetActiveByCardiMemberIdAsync(_memberId).Returns([]);

        // Not abandoned by a pending deletion, unless a test says otherwise: a member whose
        // watchers have all asked for their accounts to go is not monitored at all, so without
        // this every case here would be testing the wrong thing. The substitute's own default is
        // already false, but saying it makes the dependency visible where the cases below read.
        _links.IsLeftUnwatchedByPendingDeletionAsync(Arg.Any<Guid>()).Returns(false);

        // Defaults: one active London-anchored member whose device has been silent for two and
        // a half hours, with no standing alerts.
        _members.GetActiveIdsWithActivitySinceAsync(Arg.Any<DateOnly>()).Returns([_memberId]);
        _members.GetByIdAsync(_memberId).Returns(Member());
        SetupAnchorTimeZone("Europe/London");
        SetupLastDataAt(UtcNow.AddMinutes(-150));
        _alerts.GetByCardiMemberAsync(_memberId, activeOnly: false).Returns([]);
    }

    private CardiMember Member() => new()
    {
        Id = _memberId,
        FirstName = "Margaret",
        LastName = "Doe",
        DateOfBirth = new DateOnly(1948, 3, 2),
        IsActive = true,
    };

    private void SetupAnchorTimeZone(string timeZoneId)
    {
        _links.GetByCardiMemberIdAsync(_memberId).Returns(
        [
            new UserCardiMember { UserId = _userId, CardiMemberId = _memberId, IsActive = true },
        ]);
        _users.GetByIdAsync(_userId).Returns(new User { Id = _userId, TimeZoneId = timeZoneId });
    }

    /// <summary>Builds the fetched window so the last granular reading ends at
    /// <paramref name="lastDataUtc"/> (null for a window with no readings at all).</summary>
    private void SetupLastDataAt(DateTime? lastDataUtc)
    {
        _granular.GetWindowAsync(_memberId, Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var fromUtc = call.ArgAt<DateTime>(1);
                var toUtc = call.ArgAt<DateTime>(2);
                var minutes = (int)(toUtc - fromUtc).TotalMinutes;
                var series = new Dictionary<GranularMetric, float?[]>();
                if (lastDataUtc is not null)
                {
                    var hr = new float?[minutes];
                    var index = (int)(lastDataUtc.Value - fromUtc).TotalMinutes - 1;
                    if (index >= 0 && index < minutes)
                        hr[index] = 72f;
                    series[GranularMetric.HeartRate] = hr;
                }

                return new GranularWindow
                {
                    CardiMemberId = _memberId,
                    FromUtc = fromUtc,
                    ToUtc = toUtc,
                    MinuteSeries = series,
                };
            });
    }

    /// <summary>
    /// Resolves the sync engine exactly as production does — keyed by the HealthApi the device
    /// type maps to, through a real container. A substitute <c>IServiceProvider</c> would have
    /// happily returned the mock for a registration that does not exist, which is precisely the
    /// mistake that took the worker down: <see cref="IDeviceSyncService"/> is keyed, and asking
    /// for it unkeyed throws at activation.
    /// </summary>
    private IServiceProvider BuildServices() =>
        new ServiceCollection()
            .AddSingleton<IOptions<List<DeviceProviderSettings>>>(Options.Create(
                new List<DeviceProviderSettings>
                {
                    new()
                    {
                        Provider = nameof(HealthApi.GoogleHealth),
                        DeviceTypes = [nameof(DeviceType.Fitbit)],
                    },
                }))
            .AddKeyedSingleton(HealthApi.GoogleHealth, _deviceSync)
            .BuildServiceProvider();

    private InactivityDetectionService CreateSut() =>
        new(_unitOfWork, Substitute.For<IDispatchService>(), BuildServices(),
            NullLogger<InactivityDetectionService>.Instance);

    /// <summary>
    /// Gives the member a connected device, so the pre-alert probe has something to pull. Without
    /// one there is nothing to ask, and detection falls straight through to the alert.
    /// </summary>
    private DeviceConnection SetupConnectedDevice()
    {
        var connection = new DeviceConnection
        {
            Id = Guid.NewGuid(),
            CardiMemberId = _memberId,
            DeviceType = DeviceType.Fitbit,
            ConnectionStatus = ConnectionStatus.Connected,
            IsActive = true,
        };
        _connections.GetActiveByCardiMemberIdAsync(_memberId).Returns([connection]);
        return connection;
    }

    /// <summary>
    /// The probe that stands between a stalled puller and a false alarm: a forced sync that
    /// actually brings data back means the device was never silent, only unfetched.
    /// </summary>
    [Fact]
    public async Task AProbePullThatFindsReadings_RaisesNoAlert()
    {
        SetupConnectedDevice();
        _deviceSync
            .When(s => s.SyncCardiMemberAsync(Arg.Any<DeviceConnection>(), Arg.Any<SyncScope>()))
            .Do(_ => SetupLastDataAt(UtcNow.AddMinutes(-5)));

        var raised = await CreateSut().DetectAsync(UtcNow, Rules);

        Assert.Equal(0, raised);
        await _deviceSync.Received(1).SyncCardiMemberAsync(Arg.Any<DeviceConnection>(), Arg.Any<SyncScope>());
        await _alerts.DidNotReceive().AddAsync(Arg.Any<Alert>());
    }

    /// <summary>
    /// Asking to delete your account stops monitoring for anyone it leaves without a caregiver —
    /// and this pass is the one place that reaches the sync service without going through the
    /// scheduler that already applies that rule. Both halves matter: no pull, because it writes
    /// health rows for somebody we have been asked to stop collecting for, and no alert, because
    /// there is nobody left to send it to.
    /// </summary>
    [Fact]
    public async Task AMemberLeftWithoutACaregiverByAPendingDeletion_IsNeitherProbedNorAlerted()
    {
        SetupConnectedDevice();
        _links.IsLeftUnwatchedByPendingDeletionAsync(_memberId).Returns(true);

        var raised = await CreateSut().DetectAsync(UtcNow, Rules);

        Assert.Equal(0, raised);
        await _deviceSync.DidNotReceiveWithAnyArgs()
            .SyncCardiMemberAsync(default!, default);
        await _alerts.DidNotReceive().AddAsync(Arg.Any<Alert>());
    }

    /// <summary>
    /// The other side of it: one caregiver deleting their account does not stop monitoring for a
    /// member somebody else still watches. This is also the case that pins the predicate to the
    /// scheduler's — a member with no active link at all answers the same way, and is still
    /// probed, because routine sync keeps collecting for them too.
    /// </summary>
    [Fact]
    public async Task AMemberNotAbandonedByAPendingDeletion_IsProbedAndAlertedAsUsual()
    {
        SetupConnectedDevice();
        _links.IsLeftUnwatchedByPendingDeletionAsync(_memberId).Returns(false);

        var raised = await CreateSut().DetectAsync(UtcNow, Rules);

        Assert.Equal(1, raised);
        await _deviceSync.Received(1).SyncCardiMemberAsync(Arg.Any<DeviceConnection>(), Arg.Any<SyncScope>());
    }

    [Fact]
    public async Task AMemberWhoseDevicesAreAllSuspended_IsNeitherProbedNorAlerted()
    {
        // Collection stopped because a caregiver stopped it; "the watch has gone quiet" would be
        // telling them something they did themselves.
        var device = SetupConnectedDevice();
        device.SuspendedAt = UtcNow.AddHours(-3);

        var raised = await CreateSut().DetectAsync(UtcNow, Rules);

        Assert.Equal(0, raised);
        await _deviceSync.DidNotReceiveWithAnyArgs().SyncCardiMemberAsync(default!, default);
        await _alerts.DidNotReceive().AddAsync(Arg.Any<Alert>());
    }

    /// <summary>A pull that runs and still finds nothing is what makes the alert trustworthy.</summary>
    [Fact]
    public async Task AProbePullThatFindsNothing_StillRaisesTheAlert()
    {
        SetupConnectedDevice();

        var raised = await CreateSut().DetectAsync(UtcNow, Rules);

        Assert.Equal(1, raised);
        await _deviceSync.Received(1).SyncCardiMemberAsync(Arg.Any<DeviceConnection>(), Arg.Any<SyncScope>());
        await _alerts.Received(1).AddAsync(Arg.Any<Alert>());
    }

    /// <summary>
    /// A device that cannot even be reached is precisely who the alert is for, so a throwing pull
    /// must not swallow it.
    /// </summary>
    [Fact]
    public async Task AProbePullThatThrows_StillRaisesTheAlert()
    {
        SetupConnectedDevice();
        _deviceSync
            .When(s => s.SyncCardiMemberAsync(Arg.Any<DeviceConnection>(), Arg.Any<SyncScope>()))
            .Do(_ => throw new HttpRequestException("provider unreachable"));

        var raised = await CreateSut().DetectAsync(UtcNow, Rules);

        Assert.Equal(1, raised);
        await _alerts.Received(1).AddAsync(Arg.Any<Alert>());
    }

    /// <summary>
    /// The probe costs a provider request, so it only runs for a member already believed dark —
    /// never for one whose readings are current.
    /// </summary>
    [Fact]
    public async Task RecentReadings_AreNotProbed()
    {
        SetupConnectedDevice();
        SetupLastDataAt(UtcNow.AddMinutes(-30));

        await CreateSut().DetectAsync(UtcNow, Rules);

        await _deviceSync.DidNotReceive().SyncCardiMemberAsync(
            Arg.Any<DeviceConnection>(), Arg.Any<SyncScope>());
    }

    [Fact]
    public async Task ASilentDevice_DuringWakingHours_RaisesOneYellowDeviceCheckAlert()
    {
        var raised = await CreateSut().DetectAsync(UtcNow, Rules);

        Assert.Equal(1, raised);
        await _alerts.Received(1).AddAsync(Arg.Is<Alert>(a =>
            a.CardiMemberId == _memberId
            && a.AlertType == AlertType.Inactivity
            && a.Severity == AlertSeverity.Yellow
            // Last data 11:10 UTC = 12:10 in London (BST) — the message speaks the member's clock.
            && a.Message.Contains("since 12:10")
            && a.MetricValues!.Contains("thresholdMinutes")));
        await _unitOfWork.Received(1).SaveChangesAsync();
    }

    [Fact]
    public async Task RecentReadings_MeanNoAlert()
    {
        SetupLastDataAt(UtcNow.AddMinutes(-30));

        var raised = await CreateSut().DetectAsync(UtcNow, Rules);

        Assert.Equal(0, raised);
        await _alerts.DidNotReceive().AddAsync(Arg.Any<Alert>());
    }

    // Silence one minute short of the threshold is not yet silence — the alert fires on the
    // first pass strictly beyond it.
    [Fact]
    public async Task DataJustInsideTheThreshold_IsNotYetSilence()
    {
        SetupLastDataAt(UtcNow.AddMinutes(-Rules.SilenceThresholdMinutes).AddMinutes(1));

        var raised = await CreateSut().DetectAsync(UtcNow, Rules);

        Assert.Equal(0, raised);
    }

    [Fact]
    public async Task AWindowWithNoReadingsAtAll_CountsAsSilence()
    {
        SetupLastDataAt(null);

        var raised = await CreateSut().DetectAsync(UtcNow, Rules);

        Assert.Equal(1, raised);
        await _alerts.Received(1).AddAsync(Arg.Is<Alert>(a => a.Message.Contains("for several hours")));
    }

    // Overnight silence is a charging watch. 23:30 in London is outside waking hours even
    // though it is only 22:30 UTC — the check runs on the member's clock, not the server's.
    [Fact]
    public async Task SilenceOutsideWakingHours_IsIgnored()
    {
        var lateEvening = new DateTime(2026, 8, 10, 22, 30, 0, DateTimeKind.Utc);
        SetupLastDataAt(lateEvening.AddHours(-3));

        var raised = await CreateSut().DetectAsync(lateEvening, Rules);

        Assert.Equal(0, raised);
    }

    // At 08:00 local the trailing two hours are mostly night: alerting cannot start before
    // wakingStart + threshold, or the first alert of every day would flag the charger.
    [Fact]
    public async Task EarlyMorning_BeforeTheThresholdHasFitInsideWakingHours_IsIgnored()
    {
        var earlyMorning = new DateTime(2026, 8, 10, 7, 0, 0, DateTimeKind.Utc);  // 08:00 London
        SetupLastDataAt(null);

        var raised = await CreateSut().DetectAsync(earlyMorning, Rules);

        Assert.Equal(0, raised);
    }

    [Fact]
    public async Task AnUnresolvedInactivityAlert_SuppressesANewOne()
    {
        _alerts.GetByCardiMemberAsync(_memberId, activeOnly: false).Returns(
        [
            new Alert { CardiMemberId = _memberId, AlertType = AlertType.Inactivity, IsResolved = false },
        ]);

        var raised = await CreateSut().DetectAsync(UtcNow, Rules);

        Assert.Equal(0, raised);
        await _alerts.DidNotReceive().AddAsync(Arg.Any<Alert>());
    }

    // Removing the card dismisses this silence episode — the same dead watch must not page
    // again on the next tick. The path re-arms when the device reports again.
    [Fact]
    public async Task ADeletedUnresolvedDeviceSilenceAlert_StillSuppresses()
    {
        _alerts.GetByCardiMemberAsync(_memberId, activeOnly: false).Returns(
        [
            new Alert
            {
                CardiMemberId = _memberId, AlertType = AlertType.Inactivity, IsResolved = false,
                IsActive = false,
                MetricValues = """{"rule":"device_silence","thresholdMinutes":120}""",
            },
        ]);

        var raised = await CreateSut().DetectAsync(UtcNow, Rules);

        Assert.Equal(0, raised);
        await _alerts.DidNotReceive().AddAsync(Arg.Any<Alert>());
    }

    [Fact]
    public async Task ADeviceReportingAgain_ResolvesADeletedDeviceSilenceAlert()
    {
        var deleted = new Alert
        {
            CardiMemberId = _memberId,
            AlertType = AlertType.Inactivity,
            IsResolved = false,
            IsActive = false,
            MetricValues = """{"rule":"device_silence","thresholdMinutes":120}""",
        };
        _alerts.GetByCardiMemberAsync(_memberId, activeOnly: false).Returns([deleted]);
        SetupLastDataAt(UtcNow.AddMinutes(-10));

        var raised = await CreateSut().DetectAsync(UtcNow, Rules);

        Assert.Equal(0, raised);
        Assert.True(deleted.IsResolved);
        await _unitOfWork.Received().SaveChangesAsync();
        await _alerts.DidNotReceive().AddAsync(Arg.Any<Alert>());
    }

    /// <summary>
    /// #1249. Resolution is about an episode already running, so none of the gates that decide
    /// whether to <em>start</em> one may stand in front of it. This is the reported case: the
    /// watch was reconnected in the evening, readings came back, and the alert stood all night
    /// because 23:30 London is outside waking hours.
    /// </summary>
    [Fact]
    public async Task ADeviceReportingAgain_ResolvesItsAlert_OutsideWakingHours()
    {
        var standing = DeviceSilenceAlert();
        _alerts.GetByCardiMemberAsync(_memberId, activeOnly: false).Returns([standing]);

        var lateEvening = new DateTime(2026, 8, 10, 22, 30, 0, DateTimeKind.Utc);  // 23:30 London
        SetupLastDataAt(lateEvening.AddMinutes(-10));

        var raised = await CreateSut().DetectAsync(lateEvening, Rules);

        Assert.Equal(0, raised);
        Assert.True(standing.IsResolved);
        await _unitOfWork.Received().SaveChangesAsync();
    }

    // The other half of the same gate: before wakingStart + threshold no alert may be raised,
    // and a standing one must still be closeable.
    [Fact]
    public async Task ADeviceReportingAgain_ResolvesItsAlert_InTheEarlyMorning()
    {
        var standing = DeviceSilenceAlert();
        _alerts.GetByCardiMemberAsync(_memberId, activeOnly: false).Returns([standing]);

        var earlyMorning = new DateTime(2026, 8, 10, 7, 0, 0, DateTimeKind.Utc);  // 08:00 London
        SetupLastDataAt(earlyMorning.AddMinutes(-10));

        var raised = await CreateSut().DetectAsync(earlyMorning, Rules);

        Assert.Equal(0, raised);
        Assert.True(standing.IsResolved);
    }

    // Turning the rule off says "stop telling me about this", not "leave the last one up for
    // good". The episode has demonstrably ended, so it closes whatever the switch says.
    [Fact]
    public async Task ADeviceReportingAgain_ResolvesItsAlert_EvenWithTheRuleDisabled()
    {
        var standing = DeviceSilenceAlert();
        _alerts.GetByCardiMemberAsync(_memberId, activeOnly: false).Returns([standing]);
        _alertPreferences.GetByCardiMemberIdAsync(_memberId).Returns(new AlertPreference
        {
            CardiMemberId = _memberId,
            DisabledRules = """["device_silence"]""",
        });
        SetupLastDataAt(UtcNow.AddMinutes(-10));

        var raised = await CreateSut().DetectAsync(UtcNow, Rules);

        Assert.Equal(0, raised);
        Assert.True(standing.IsResolved);
    }

    // Still silent outside waking hours is still the cooldown: nothing resolves, nothing is
    // raised, and nothing is written. The fix above must not turn a quiet night into a write.
    [Fact]
    public async Task AStillSilentDevice_OutsideWakingHours_ResolvesNothingAndWritesNothing()
    {
        var standing = DeviceSilenceAlert();
        _alerts.GetByCardiMemberAsync(_memberId, activeOnly: false).Returns([standing]);

        var lateEvening = new DateTime(2026, 8, 10, 22, 30, 0, DateTimeKind.Utc);
        SetupLastDataAt(lateEvening.AddHours(-3));

        var raised = await CreateSut().DetectAsync(lateEvening, Rules);

        Assert.Equal(0, raised);
        Assert.False(standing.IsResolved);
        await _unitOfWork.DidNotReceive().SaveChangesAsync();
        await _alerts.DidNotReceive().AddAsync(Arg.Any<Alert>());
    }

    private Alert DeviceSilenceAlert() => new()
    {
        CardiMemberId = _memberId,
        AlertType = AlertType.Inactivity,
        IsResolved = false,
        MetricValues = """{"rule":"device_silence","thresholdMinutes":120}""",
    };

    [Fact]
    public async Task AResolvedInactivityAlert_DoesNotSuppress()
    {
        _alerts.GetByCardiMemberAsync(_memberId, activeOnly: false).Returns(
        [
            new Alert { CardiMemberId = _memberId, AlertType = AlertType.Inactivity, IsResolved = true },
        ]);

        var raised = await CreateSut().DetectAsync(UtcNow, Rules);

        Assert.Equal(1, raised);
    }

    // An unresolved HEART-RATE alert must not suppress a device-check: they answer different
    // questions ("is the heart worrying" vs "is the device even reporting").
    [Fact]
    public async Task AnUnresolvedAlertOfAnotherType_DoesNotSuppress()
    {
        _alerts.GetByCardiMemberAsync(_memberId, activeOnly: false).Returns(
        [
            new Alert { CardiMemberId = _memberId, AlertType = AlertType.HeartRate, IsResolved = false },
        ]);

        var raised = await CreateSut().DetectAsync(UtcNow, Rules);

        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task APausedMember_IsNotChecked()
    {
        var paused = Member();
        paused.MonitoringPausedUntil = UtcNow.AddDays(1);
        _members.GetByIdAsync(_memberId).Returns(paused);

        var raised = await CreateSut().DetectAsync(UtcNow, Rules);

        Assert.Equal(0, raised);
        await _granular.DidNotReceive().GetWindowAsync(
            Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DisabledDeviceSilenceRule_IsNotChecked()
    {
        _alertPreferences.GetByCardiMemberIdAsync(_memberId).Returns(new AlertPreference
        {
            CardiMemberId = _memberId,
            DisabledRules = """["device_silence"]""",
        });

        var raised = await CreateSut().DetectAsync(UtcNow, Rules);

        Assert.Equal(0, raised);
        await _granular.DidNotReceive().GetWindowAsync(
            Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
        await _alerts.DidNotReceive().AddAsync(Arg.Any<Alert>());
    }

    // Cancellation during a member's check is shutdown, not a member failure — the pass must
    // stop, not log an error and stumble to the next member.
    [Fact]
    public async Task CancellationMidPass_AbortsThePass_InsteadOfBeingSwallowed()
    {
        using var cts = new CancellationTokenSource();
        var otherId = Guid.NewGuid();
        _members.GetActiveIdsWithActivitySinceAsync(Arg.Any<DateOnly>()).Returns([_memberId, otherId]);
        _members.GetByIdAsync(_memberId).Returns<CardiMember?>(_ =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            CreateSut().DetectAsync(UtcNow, Rules, cts.Token));

        await _members.DidNotReceive().GetByIdAsync(otherId);
    }

    // The probe runs per member every 15 minutes — its cost is the fetch span, so the span is
    // pinned: rounded-up threshold + 1 hour of whole-hour slack, nothing more.
    [Fact]
    public async Task TheGranularProbe_FetchesThresholdPlusOneHourOfSlack()
    {
        await CreateSut().DetectAsync(UtcNow, Rules);

        // 13:40 UTC, 120-minute threshold → whole-hour range 11:00–14:00 (3 hours).
        await _granular.Received(1).GetWindowAsync(
            _memberId,
            new DateTime(2026, 8, 10, 11, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 8, 10, 14, 0, 0, DateTimeKind.Utc),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OneMemberFailure_DoesNotCostTheRestThePass()
    {
        var otherId = Guid.NewGuid();
        _members.GetActiveIdsWithActivitySinceAsync(Arg.Any<DateOnly>()).Returns([otherId, _memberId]);
        _members.GetByIdAsync(otherId).Throws(new InvalidOperationException("db hiccup"));

        var raised = await CreateSut().DetectAsync(UtcNow, Rules);

        Assert.Equal(1, raised);
    }

    [Theory]
    [InlineData(0, 7, 22)]
    [InlineData(-1, 7, 22)]
    [InlineData(120, -1, 22)]
    [InlineData(120, 7, 25)]
    [InlineData(120, 22, 7)]
    public async Task NonsenseRules_AreRefusedOutright(int threshold, int start, int end)
    {
        var rules = new InactivityDetectionRules
        {
            SilenceThresholdMinutes = threshold,
            WakingStartHour = start,
            WakingEndHour = end,
        };

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            CreateSut().DetectAsync(UtcNow, rules));
    }
}
