using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.ExternalClients;
using CardiTrack.Infrastructure.Services;
using NSubstitute;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// Pins the raw-then-derived shape: the device's hour vectors are written first, then the
/// member's rollups are recomputed from the <em>merged</em> window — never from the one device's
/// rows, which would overwrite a member-level figure with a single-device view.
/// </summary>
public class GranularIngestionServiceTests
{
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IGranularMetricRepository _granularMetrics = Substitute.For<IGranularMetricRepository>();

    private readonly DeviceConnection _connection = new()
    {
        Id = Guid.NewGuid(),
        CardiMemberId = Guid.NewGuid(),
    };

    private static readonly DateTime Hour = new(2026, 8, 10, 9, 0, 0, DateTimeKind.Utc);

    public GranularIngestionServiceTests()
    {
        _unitOfWork.GranularMetrics.Returns(_granularMetrics);
    }

    private GranularIngestionService CreateSut() => new(_unitOfWork);

    [Fact]
    public async Task IngestDay_UpsertsHours_ThenRecomputesRollupsFromTheMergedWindow()
    {
        var day = new DeviceGranularDay(
            HeartRate:
            [
                new GranularSample(Hour.AddMinutes(1), 70f),
                new GranularSample(Hour.AddHours(2).AddMinutes(3), 74f),
            ],
            Steps: [], ActiveZoneMinutes: [], SpO2: [], HeartRateVariability: []);

        // The merged window the repository reports back — what the rollups must be derived from.
        var mergedSeries = new float?[180];
        mergedSeries[1] = 68f;   // another device won this minute in the merge
        mergedSeries[123] = 74f;
        _granularMetrics
            .GetWindowAsync(_connection.CardiMemberId, Hour, Hour.AddHours(3), Arg.Any<CancellationToken>())
            .Returns(new GranularWindow
            {
                CardiMemberId = _connection.CardiMemberId,
                FromUtc = Hour,
                ToUtc = Hour.AddHours(3),
                MinuteSeries = new Dictionary<GranularMetric, float?[]>
                {
                    [GranularMetric.HeartRate] = mergedSeries,
                },
            });

        await CreateSut().IngestDayAsync(_connection, day);

        await _granularMetrics.Received(1).UpsertHoursAsync(
            Arg.Is<IReadOnlyList<GranularMetricHour>>(hours =>
                hours.Count == 2 &&
                hours.All(h => h.DeviceConnectionId == _connection.Id)),
            Arg.Any<CancellationToken>());

        // The window read spans exactly the touched hours, closed-open.
        await _granularMetrics.Received(1).GetWindowAsync(
            _connection.CardiMemberId, Hour, Hour.AddHours(3), Arg.Any<CancellationToken>());

        // Rollups carry the merged value (68), not this device's own reading (70).
        await _granularMetrics.Received(1).UpsertRollupsAsync(
            Arg.Is<IReadOnlyList<MetricRollupHourly>>(rollups =>
                rollups.Count == 2 &&
                rollups.Single(r => r.HourStartUtc == Hour).Avg == 68f &&
                rollups.Single(r => r.HourStartUtc == Hour.AddHours(2)).Avg == 74f),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IngestDay_DoesNothing_ForAnEmptyDay()
    {
        await CreateSut().IngestDayAsync(_connection, DeviceGranularDay.Empty);

        await _granularMetrics.DidNotReceive().UpsertHoursAsync(
            Arg.Any<IReadOnlyList<GranularMetricHour>>(), Arg.Any<CancellationToken>());
        await _granularMetrics.DidNotReceive().GetWindowAsync(
            Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }
}
