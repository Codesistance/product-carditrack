using CardiTrack.Application.Interfaces.Clients;
using CardiTrack.Infrastructure.Settings;
using Google;
using Google.Cloud.Storage.V1;
using Microsoft.Extensions.Logging;

namespace CardiTrack.Infrastructure.ExternalClients.Storage;

/// <summary>
/// Rendered health-data exports in a private GCS bucket. Authenticates via Application Default
/// Credentials, the same way <see cref="GcsProfilePhotoStorage"/> does.
/// <para>
/// The one deliberate difference from the photo adapter is what is missing: there is no signed-URL
/// read. An export is a complete identified health record, and a signed URL would be a bearer
/// capability to it — handing one out puts the file outside our authorization check and outside the
/// audit trail that records who read whose record. Bytes are therefore read back through
/// <see cref="DownloadAsync"/> and streamed by the API, which pays for the proxy in bandwidth and
/// gets an <c>[AuditHealthDataAccess]</c> row for every retrieval in exchange.
/// </para>
/// <para>
/// Failure stance is uniform and loud: an unconfigured bucket throws on every operation. Photos
/// degrade to an initials avatar because they decorate a screen whose real payload is elsewhere;
/// an export has no such fallback — it is the payload.
/// </para>
/// </summary>
public class GcsReportStorage : IReportStorage
{
    private readonly ReportStorageOptions _options;
    private readonly ILogger<GcsReportStorage> _logger;
    private readonly Lazy<Task<StorageClient>> _client;

    public GcsReportStorage(ReportStorageOptions options, ILogger<GcsReportStorage> logger)
    {
        _options = options;
        _logger = logger;
        // Lazy so a credential-less host (every local machine) can still construct the service
        // graph; ADC is only resolved on the first actual storage call.
        _client = new Lazy<Task<StorageClient>>(() => StorageClient.CreateAsync());
    }

    public async Task<string> UploadAsync(
        Guid ownerUserId,
        string reportId,
        string extension,
        string contentType,
        ReadOnlyMemory<byte> content,
        CancellationToken ct = default)
    {
        var bucket = RequireBucket();
        var objectName = $"reports/{ownerUserId}/{reportId}.{extension}";

        var client = await _client.Value;
        using var stream = new MemoryStream(content.ToArray(), writable: false);
        await client.UploadObjectAsync(bucket, objectName, contentType, stream, cancellationToken: ct);

        return objectName;
    }

    public async Task<byte[]?> DownloadAsync(string objectName, CancellationToken ct = default)
    {
        var bucket = RequireBucket();

        try
        {
            var client = await _client.Value;
            using var stream = new MemoryStream();
            await client.DownloadObjectAsync(bucket, objectName, stream, cancellationToken: ct);
            return stream.ToArray();
        }
        catch (GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // A row outliving its object — a partially-completed cleanup, or a lifecycle rule that
            // moved first. The caller turns this into the same "expired" answer an evicted report
            // gives, which is the truth from the caregiver's side.
            _logger.LogWarning("Report object {ObjectName} is missing from the export bucket.", objectName);
            return null;
        }
    }

    public async Task DeleteAsync(string objectName, CancellationToken ct = default)
    {
        var bucket = RequireBucket();

        try
        {
            var client = await _client.Value;
            await client.DeleteObjectAsync(bucket, objectName, cancellationToken: ct);
        }
        catch (GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Already gone is the outcome delete exists to produce — so a re-run of an
            // interrupted cleanup sweep is idempotent rather than a page of failures.
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to delete report object {ObjectName}.", objectName);
            throw;
        }
    }

    private string RequireBucket() =>
        string.IsNullOrWhiteSpace(_options.Bucket)
            ? throw new InvalidOperationException(
                "Report storage is not configured: set 'Storage:Reports:Bucket' (environment " +
                "variable 'Storage__Reports__Bucket') to the report-exports bucket name. " +
                "Health data export is unavailable until it is set.")
            : _options.Bucket;
}
