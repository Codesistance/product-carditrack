using CardiTrack.Application.Interfaces.Clients;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.ExternalClients;
using CardiTrack.Infrastructure.Services;
using CardiTrack.Infrastructure.Settings;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// Pins the enrichment pass's privacy guarantees, not just its happy path: a member who has not
/// granted <see cref="CardiMember.EnvironmentalContextConsentGranted"/> is never queried for
/// exercise or GPS data, a connection without the location scope is skipped even for a
/// consented member, and the stored reading never carries a coordinate.
/// </summary>
public class EnvironmentalEnrichmentServiceTests
{
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly ICardiMemberRepository _members = Substitute.For<ICardiMemberRepository>();
    private readonly IDeviceConnectionRepository _connections = Substitute.For<IDeviceConnectionRepository>();
    private readonly IEnvironmentalReadingRepository _readings = Substitute.For<IEnvironmentalReadingRepository>();
    private readonly IOAuthTokenRefreshService _tokenRefresh = Substitute.For<IOAuthTokenRefreshService>();
    private readonly IDeviceApiClient _deviceApi = Substitute.For<IDeviceApiClient>();
    private readonly IEnvironmentalContextClient _environmentalContext = Substitute.For<IEnvironmentalContextClient>();

    private readonly Guid _memberId = Guid.NewGuid();
    private static readonly DateTime UtcNow = new(2026, 8, 12, 10, 0, 0, DateTimeKind.Utc);

    private readonly EnvironmentalEnrichmentService _service;

    public EnvironmentalEnrichmentServiceTests()
    {
        _unitOfWork.CardiMembers.Returns(_members);
        _unitOfWork.DeviceConnections.Returns(_connections);
        _unitOfWork.EnvironmentalReadings.Returns(_readings);

        var providers = new List<DeviceProviderSettings> { new() { Provider = "GoogleHealth", DeviceTypes = ["Fitbit"] } };

        _service = new EnvironmentalEnrichmentService(
            _unitOfWork, _tokenRefresh, _deviceApi, _environmentalContext,
            Options.Create(providers), Microsoft.Extensions.Logging.Abstractions.NullLogger<EnvironmentalEnrichmentService>.Instance);
    }

    private CardiMember Member(bool consent = true) => new()
    {
        Id = _memberId,
        IsActive = true,
        EnvironmentalContextConsentGranted = consent,
    };

    private DeviceConnection Connection(bool locationScope) => new()
    {
        Id = Guid.NewGuid(),
        CardiMemberId = _memberId,
        DeviceType = DeviceType.Fitbit,
        IsActive = true,
        Scopes = locationScope
            ? """["https://www.googleapis.com/auth/googlehealth.activity_and_fitness.readonly","https://www.googleapis.com/auth/googlehealth.location.readonly"]"""
            : """["https://www.googleapis.com/auth/googlehealth.activity_and_fitness.readonly"]""",
    };

    [Fact]
    public async Task NoMemberIsProcessed_WhenNoneHaveGrantedConsent()
    {
        _members.GetActiveIdsWithEnvironmentalConsentAsync().Returns([]);

        var written = await _service.EnrichDueSessionsAsync(UtcNow);

        Assert.Equal(0, written);
        await _connections.DidNotReceiveWithAnyArgs().GetByCardiMemberIdAsync(default);
    }

    [Fact]
    public async Task AConsentedMembersConnection_WithoutTheLocationScope_IsNeverAskedForExerciseSessions()
    {
        _members.GetActiveIdsWithEnvironmentalConsentAsync().Returns([_memberId]);
        _members.GetByIdAsync(_memberId).Returns(Member());
        _connections.GetByCardiMemberIdAsync(_memberId).Returns([Connection(locationScope: false)]);

        var written = await _service.EnrichDueSessionsAsync(UtcNow);

        Assert.Equal(0, written);
        await _deviceApi.DidNotReceiveWithAnyArgs().GetExerciseSessionsAsync(default!, default);
    }

    [Fact]
    public async Task ASessionWithNoGpsTrack_IsSkipped_AndNeverReachesTheEnvironmentalClient()
    {
        _members.GetActiveIdsWithEnvironmentalConsentAsync().Returns([_memberId]);
        _members.GetByIdAsync(_memberId).Returns(Member());
        _connections.GetByCardiMemberIdAsync(_memberId).Returns([Connection(locationScope: true)]);
        _tokenRefresh.RefreshIfExpiredAsync(Arg.Any<DeviceConnection>(), Arg.Any<DeviceProviderSettings>())
            .Returns("token");
        _deviceApi.GetExerciseSessionsAsync(Arg.Any<string>(), Arg.Any<DateOnly>()).Returns(
            [new ExerciseSession("s1", UtcNow.AddHours(-1), UtcNow.AddHours(-1).AddMinutes(30), HasGpsTrack: false)]);

        var written = await _service.EnrichDueSessionsAsync(UtcNow);

        Assert.Equal(0, written);
        await _deviceApi.DidNotReceiveWithAnyArgs().GetExerciseGpsPointAsync(default!, default!);
        await _environmentalContext.DidNotReceiveWithAnyArgs()
            .GetContextAsync(default, default, default, default);
    }

    [Fact]
    public async Task AGpsTaggedSession_IsEnriched_AndTheStoredReadingCarriesOnlyDerivedValues()
    {
        var sessionStart = UtcNow.AddHours(-1);
        var session = new ExerciseSession("s1", sessionStart, sessionStart.AddMinutes(30), HasGpsTrack: true);

        _members.GetActiveIdsWithEnvironmentalConsentAsync().Returns([_memberId]);
        _members.GetByIdAsync(_memberId).Returns(Member());
        _connections.GetByCardiMemberIdAsync(_memberId).Returns([Connection(locationScope: true)]);
        _tokenRefresh.RefreshIfExpiredAsync(Arg.Any<DeviceConnection>(), Arg.Any<DeviceProviderSettings>())
            .Returns("token");
        _deviceApi.GetExerciseSessionsAsync("token", Arg.Any<DateOnly>()).Returns(
            callInfo => callInfo.ArgAt<DateOnly>(1) == DateOnly.FromDateTime(UtcNow) ? [session] : []);
        _readings.ExistsAsync(_memberId, sessionStart, Arg.Any<CancellationToken>()).Returns(false);
        _deviceApi.GetExerciseGpsPointAsync("token", "s1")
            .Returns(new ExerciseGpsPoint(51.5, -0.12, sessionStart.AddMinutes(5)));
        _environmentalContext.GetContextAsync(51.5, -0.12, sessionStart.AddMinutes(5), Arg.Any<CancellationToken>())
            .Returns(new EnvironmentalContext(TemperatureCelsius: 28.4, AirQualityIndex: 42, AirQualityCategory: "Good"));

        var written = await _service.EnrichDueSessionsAsync(UtcNow);

        Assert.Equal(1, written);
        await _readings.Received(1).UpsertAsync(
            Arg.Is<EnvironmentalReading>(r =>
                r.CardiMemberId == _memberId &&
                r.SessionStartUtc == sessionStart &&
                r.TemperatureCelsius == 28.4 &&
                r.AirQualityIndex == 42 &&
                r.AirQualityCategory == "Good"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnAlreadyEnrichedSession_IsNotEnrichedAgain()
    {
        var sessionStart = UtcNow.AddHours(-1);
        var session = new ExerciseSession("s1", sessionStart, sessionStart.AddMinutes(30), HasGpsTrack: true);

        _members.GetActiveIdsWithEnvironmentalConsentAsync().Returns([_memberId]);
        _members.GetByIdAsync(_memberId).Returns(Member());
        _connections.GetByCardiMemberIdAsync(_memberId).Returns([Connection(locationScope: true)]);
        _tokenRefresh.RefreshIfExpiredAsync(Arg.Any<DeviceConnection>(), Arg.Any<DeviceProviderSettings>())
            .Returns("token");
        _deviceApi.GetExerciseSessionsAsync(Arg.Any<string>(), Arg.Any<DateOnly>()).Returns([session]);
        _readings.ExistsAsync(_memberId, sessionStart, Arg.Any<CancellationToken>()).Returns(true);

        var written = await _service.EnrichDueSessionsAsync(UtcNow);

        Assert.Equal(0, written);
        await _deviceApi.DidNotReceiveWithAnyArgs().GetExerciseGpsPointAsync(default!, default!);
    }
}
