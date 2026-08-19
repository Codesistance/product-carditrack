using CardiTrack.Application.Interfaces.Clients;
using CardiTrack.Infrastructure.Settings;
using Google;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Storage.V1;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace CardiTrack.Infrastructure.ExternalClients.Storage;

/// <summary>
/// CardiMember profile photos in a private GCS bucket. Authenticates via Application Default
/// Credentials; reads are served as V4 signed GET URLs minted with <see cref="UrlSigner"/> from
/// the same credential — on Cloud Run that signs through the IAM Credentials <c>signBlob</c> API
/// (the service account holds <c>serviceAccountTokenCreator</c> on itself), locally through the
/// developer's ADC key. Signed URLs are cached in-memory per object name so a dashboard of
/// members doesn't fan one signing call out per avatar per refresh.
/// <para>
/// Failure stance mirrors the data's direction of travel. Writes (upload/delete) are part of a
/// caregiver's save and fail loudly when the bucket isn't configured. Reads are decoration on
/// screens whose real payload is health data, so an unset bucket or a signing outage returns
/// null — the initials avatar — and is logged once at warning rather than once per member.
/// </para>
/// <para>
/// A signed URL is a bearer capability to a full-face photo (Tier 1): it must never be logged.
/// Log lines here carry object names only.
/// </para>
/// </summary>
public class GcsProfilePhotoStorage : IProfilePhotoStorage
{
    private const string ContentType = "image/jpeg";

    /// <summary>Prefixed because the API's one <see cref="IMemoryCache"/> is shared with rate limiting.</summary>
    private const string CacheKeyPrefix = "member-photo-url:";

    private readonly MemberPhotoStorageOptions _options;
    private readonly IMemoryCache _cache;
    private readonly ILogger<GcsProfilePhotoStorage> _logger;
    private readonly Lazy<Task<StorageClient>> _client;
    private readonly Lazy<Task<UrlSigner>> _signer;
    private int _readUnavailableLogged;

    public GcsProfilePhotoStorage(
        MemberPhotoStorageOptions options,
        IMemoryCache cache,
        ILogger<GcsProfilePhotoStorage> logger)
    {
        _options = options;
        _cache = cache;
        _logger = logger;
        // Lazy so an unconfigured or credential-less host (every local machine) can still
        // construct the service graph; ADC is only resolved on the first actual storage call.
        _client = new Lazy<Task<StorageClient>>(() => StorageClient.CreateAsync());
        _signer = new Lazy<Task<UrlSigner>>(async () =>
            UrlSigner.FromCredential(await GoogleCredential.GetApplicationDefaultAsync()));
    }

    public async Task<string> UploadAsync(
        Guid cardiMemberId, ReadOnlyMemory<byte> jpegBytes, CancellationToken ct = default)
    {
        var bucket = RequireBucket();
        var objectName = $"members/{cardiMemberId}/{Guid.NewGuid():N}.jpg";

        var client = await _client.Value;
        using var stream = new MemoryStream(jpegBytes.ToArray(), writable: false);
        await client.UploadObjectAsync(bucket, objectName, ContentType, stream, cancellationToken: ct);

        return objectName;
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
            // Already gone is the outcome delete exists to produce.
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Logged here — with the object name, never a URL — because the callers on
            // best-effort cleanup paths swallow this rethrow by design (see IProfilePhotoStorage).
            _logger.LogWarning(ex, "Failed to delete member photo object {ObjectName}.", objectName);
            throw;
        }
    }

    public async Task<string?> GetReadUrlAsync(string objectName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.Bucket))
        {
            LogReadUnavailableOnce(null);
            return null;
        }

        if (_cache.TryGetValue<string>(CacheKeyPrefix + objectName, out var cached))
            return cached;

        try
        {
            var signer = await _signer.Value;
            var template = UrlSigner.RequestTemplate
                .FromBucket(_options.Bucket)
                .WithObjectName(objectName)
                .WithHttpMethod(HttpMethod.Get);
            var options = UrlSigner.Options
                .FromDuration(_options.SignedUrlTtl)
                .WithSigningVersion(SigningVersion.V4);

            var url = await signer.SignAsync(template, options, ct);

            _cache.Set(CacheKeyPrefix + objectName, url, _options.ReadUrlCacheTtl);
            return url;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Missing ADC, missing tokenCreator grant, IAM Credentials outage — all the same to
            // a read surface: no photo. The screen still renders; the avatar shows initials.
            LogReadUnavailableOnce(ex);
            return null;
        }
    }

    private string RequireBucket() =>
        string.IsNullOrWhiteSpace(_options.Bucket)
            ? throw new InvalidOperationException(
                "Member photo storage is not configured: set 'Storage:MemberPhotos:Bucket' " +
                "(environment variable 'Storage__MemberPhotos__Bucket') to the member-photos " +
                "bucket name. Photo upload and removal are unavailable until it is set.")
            : _options.Bucket;

    /// <summary>
    /// Once per process, not once per member per screen — an unconfigured local run would
    /// otherwise turn every dashboard refresh into a page of identical warnings.
    /// </summary>
    private void LogReadUnavailableOnce(Exception? exception)
    {
        if (Interlocked.Exchange(ref _readUnavailableLogged, 1) == 1)
            return;

        if (exception is null)
        {
            _logger.LogWarning(
                "Member photo reads are disabled: 'Storage:MemberPhotos:Bucket' is not set. " +
                "Photo URLs will be null and clients will show initials avatars.");
        }
        else
        {
            _logger.LogWarning(exception,
                "Member photo URL signing is unavailable; photo URLs will be null and clients " +
                "will show initials avatars.");
        }
    }
}
