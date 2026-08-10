using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Entities;
using CardiTrack.Infrastructure.ExternalClients;

namespace CardiTrack.Infrastructure.Services;

/// <summary>
/// Writes one device's granular day and keeps the member-level rollups honest: the device's hour
/// vectors are upserted first, then the touched hours are re-read <em>merged across devices</em>
/// and the hourly rollups recomputed from that — the same raw-then-derived shape the daily
/// pipeline uses (`DeviceActivityLogs` → `ActivityLogs`), at hour grain. Recomputing from the
/// merged window is what makes this idempotent and order-independent across a member's devices.
/// </summary>
public class GranularIngestionService : IGranularIngestionService
{
    private readonly IUnitOfWork _unitOfWork;

    public GranularIngestionService(IUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    public async Task IngestDayAsync(
        DeviceConnection connection, DeviceGranularDay day, CancellationToken ct = default)
    {
        var hours = GranularDayBucketer.Bucket(connection.CardiMemberId, connection.Id, day);
        if (hours.Count == 0)
            return;

        await _unitOfWork.GranularMetrics.UpsertHoursAsync(hours, ct);

        var fromUtc = hours.Min(h => h.HourStartUtc);
        var toUtc = hours.Max(h => h.HourStartUtc).AddHours(1);
        var window = await _unitOfWork.GranularMetrics.GetWindowAsync(
            connection.CardiMemberId, fromUtc, toUtc, ct);

        var rollups = GranularDayBucketer.RollupsFromWindow(window);
        if (rollups.Count > 0)
            await _unitOfWork.GranularMetrics.UpsertRollupsAsync(rollups, ct);
    }
}
