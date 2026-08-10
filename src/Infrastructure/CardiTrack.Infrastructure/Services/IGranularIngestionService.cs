using CardiTrack.Domain.Entities;
using CardiTrack.Infrastructure.ExternalClients;

namespace CardiTrack.Infrastructure.Services;

/// <summary>
/// Stores one device's granular day: hour vectors for the device, then the member's hourly
/// rollups recomputed from the merged window. Infrastructure-local (like
/// <see cref="IDeviceApiClient"/>) because its input is the provider-shaped
/// <see cref="DeviceGranularDay"/>, which the Application layer never sees.
/// </summary>
public interface IGranularIngestionService
{
    Task IngestDayAsync(
        DeviceConnection connection, DeviceGranularDay day, CancellationToken ct = default);
}
