using CardiTrack.Application.Interfaces.Clients;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Infrastructure.ExternalClients;
using CardiTrack.Infrastructure.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CardiTrack.Infrastructure.Services;

/// <summary>
/// For each consent-granted member's connections with the location scope, fetches GPS-tagged
/// exercise sessions for a short trailing window, looks up ambient conditions for each new
/// session, and stores the derived reading. See <see cref="IEnvironmentalEnrichmentService"/>
/// for why this runs as its own pipeline job rather than inside the routine device sync.
/// </summary>
public class EnvironmentalEnrichmentService : IEnvironmentalEnrichmentService
{
    /// <summary>
    /// Civil days checked each pass, ending today. Two days — like the sync's own short trailing
    /// window — covers a session logged just after a UTC-day boundary without re-scanning a
    /// member's whole history every run; the natural-key upsert makes re-checking an already
    /// enriched session a no-op.
    /// </summary>
    private const int LookbackDays = 2;

    /// <summary>Scope substring identifying location access was granted for a connection —
    /// matches the full <c>https://www.googleapis.com/auth/googlehealth.location.readonly</c>
    /// scope URI without depending on its exact casing or querystring form.</summary>
    private const string LocationScopeMarker = "googlehealth.location";

    private readonly IUnitOfWork _unitOfWork;
    private readonly IOAuthTokenRefreshService _tokenRefresh;
    private readonly IDeviceApiClient _deviceApi;
    private readonly IEnvironmentalContextClient _environmentalContext;
    private readonly List<DeviceProviderSettings> _providers;
    private readonly ILogger<EnvironmentalEnrichmentService> _logger;

    public EnvironmentalEnrichmentService(
        IUnitOfWork unitOfWork,
        IOAuthTokenRefreshService tokenRefresh,
        IDeviceApiClient deviceApi,
        IEnvironmentalContextClient environmentalContext,
        IOptions<List<DeviceProviderSettings>> providers,
        ILogger<EnvironmentalEnrichmentService> logger)
    {
        _unitOfWork = unitOfWork;
        _tokenRefresh = tokenRefresh;
        _deviceApi = deviceApi;
        _environmentalContext = environmentalContext;
        _providers = providers.Value;
        _logger = logger;
    }

    public async Task<int> EnrichDueSessionsAsync(DateTime utcNow, CancellationToken ct = default)
    {
        // The consent flag is the only candidate filter — no code path past this call ever
        // fetches exercise/location data or calls the environmental client for a member who is
        // not in this list (data_protection_architecture.md).
        var memberIds = await _unitOfWork.CardiMembers.GetActiveIdsWithEnvironmentalConsentAsync();

        var enriched = 0;
        foreach (var memberId in memberIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                enriched += await EnrichMemberAsync(memberId, utcNow, ct);
            }
            catch (Exception ex)
            {
                // One member's failure — a device sync hiccup, a lookup outage — must not cost
                // the rest of the fleet this pass; the next scheduled run retries naturally.
                _logger.LogError(ex, "Environmental enrichment failed for CardiMember {CardiMemberId}.", memberId);
            }
        }

        _logger.LogInformation(
            "Environmental enrichment pass complete. Candidates: {Candidates}, readings written: {Enriched}.",
            memberIds.Count, enriched);
        return enriched;
    }

    private async Task<int> EnrichMemberAsync(Guid memberId, DateTime utcNow, CancellationToken ct)
    {
        var member = await _unitOfWork.CardiMembers.GetByIdAsync(memberId);
        if (member is null || !member.IsActive || !member.EnvironmentalContextConsentGranted
            || member.IsMonitoringPaused(utcNow))
        {
            return 0;
        }

        var connections = (await _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(memberId))
            .Where(c => c.IsActive && HasLocationScope(c))
            .ToList();

        var written = 0;
        foreach (var connection in connections)
        {
            written += await EnrichConnectionAsync(memberId, connection, utcNow, ct);
        }

        return written;
    }

    private async Task<int> EnrichConnectionAsync(
        Guid memberId, DeviceConnection connection, DateTime utcNow, CancellationToken ct)
    {
        var providerConfig = _providers.ConfigFor(connection.DeviceType);
        if (providerConfig is null)
            return 0;

        var accessToken = await _tokenRefresh.RefreshIfExpiredAsync(connection, providerConfig);

        var today = DateOnly.FromDateTime(utcNow);
        var written = 0;
        for (var day = today.AddDays(-(LookbackDays - 1)); day <= today; day = day.AddDays(1))
        {
            var sessions = await _deviceApi.GetExerciseSessionsAsync(accessToken, day);
            foreach (var session in sessions.Where(s => s.HasGpsTrack))
            {
                ct.ThrowIfCancellationRequested();
                if (await EnrichSessionAsync(memberId, connection.Id, accessToken, session, utcNow, ct))
                    written++;
            }
        }

        return written;
    }

    private async Task<bool> EnrichSessionAsync(
        Guid memberId, Guid connectionId, string accessToken, ExerciseSession session,
        DateTime utcNow, CancellationToken ct)
    {
        // The natural key is the dedup probe, the same as the real-time assessor: a session only
        // needs enriching once, ever — its location and time do not change on re-sync.
        if (await _unitOfWork.EnvironmentalReadings.ExistsAsync(memberId, session.StartUtc, ct))
            return false;

        var point = await _deviceApi.GetExerciseGpsPointAsync(accessToken, session.SessionId);
        if (point is null)
            return false;

        // The coordinate lives only in this call's stack: it is read out of `point` here and
        // never copied into any field this method returns or stores past this line.
        var context = await _environmentalContext.GetContextAsync(
            point.Latitude, point.Longitude, point.TimestampUtc, ct);

        var reading = new EnvironmentalReading
        {
            CardiMemberId = memberId,
            SessionStartUtc = session.StartUtc,
            SessionEndUtc = session.EndUtc,
            DeviceConnectionId = connectionId,
            TemperatureCelsius = context.TemperatureCelsius,
            AirQualityIndex = context.AirQualityIndex,
            AirQualityCategory = context.AirQualityCategory,
            GeneratedAtUtc = utcNow,
        };

        await _unitOfWork.EnvironmentalReadings.UpsertAsync(reading, ct);
        return true;
    }

    private static bool HasLocationScope(DeviceConnection connection) =>
        connection.Scopes.Contains(LocationScopeMarker, StringComparison.OrdinalIgnoreCase);
}
