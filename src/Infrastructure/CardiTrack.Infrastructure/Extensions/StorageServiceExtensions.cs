using CardiTrack.Application.Interfaces.Clients;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Infrastructure.ExternalClients.Storage;
using CardiTrack.Infrastructure.Services;
using CardiTrack.Infrastructure.Services.Reports;
using CardiTrack.Infrastructure.Settings;
using CardiTrack.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CardiTrack.Infrastructure.Extensions;

/// <summary>
/// Wires member profile photo storage (processor + GCS adapter). Registered unconditionally by
/// the API — unlike <see cref="EnvironmentalServiceExtensions"/> there is no fail-at-startup
/// check, because an absent bucket is a supported state, not a misconfiguration: every local
/// machine runs without one. In that state reads resolve to null (initials avatars) and photo
/// upload/removal throw a clear <see cref="InvalidOperationException"/>.
/// </summary>
public static class StorageServiceExtensions
{
    public static IServiceCollection AddMemberPhotoStorage(
        this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(ConfigurationKeys.Storage.MemberPhotosSectionName)
            .Get<MemberPhotoStorageOptions>() ?? new MemberPhotoStorageOptions();

        services.AddSingleton(options);
        // Backs the signed-URL cache. Idempotent — the API also registers it for rate limiting.
        services.AddMemoryCache();

        // Singletons: the processor is stateless, and the storage adapter's GCS client, URL
        // signer and log-once flag are all meant to live for the process.
        services.AddSingleton<IProfilePhotoProcessor, ImageSharpProfilePhotoProcessor>();
        services.AddSingleton<IProfilePhotoStorage, GcsProfilePhotoStorage>();

        return services;
    }

    /// <summary>
    /// Wires health-data export storage. Registered by the API (which generates and serves
    /// exports) and by the Worker (which reaps expired ones). Same absent-bucket stance as photos
    /// at startup — it is a supported local state, not a misconfiguration — but at call time the
    /// adapter throws rather than degrading: an export has no partial answer worth returning.
    /// </summary>
    public static IServiceCollection AddReportStorage(
        this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(ConfigurationKeys.Storage.ReportsSectionName)
            .Get<ReportStorageOptions>() ?? new ReportStorageOptions();

        services.AddSingleton(options);

        // Singleton: the adapter holds nothing per-request, and its GCS client is meant to live
        // for the process.
        services.AddSingleton<IReportStorage, GcsReportStorage>();

        return services;
    }

    /// <summary>
    /// Wires the export renderers — one per <see cref="Domain.Enums.ReportFormat"/> shipping in
    /// MVP 1. Registered by the API only; the Worker reaps expired exports but never renders one.
    /// <c>ReportGenerationService</c> resolves the whole set and picks by format, so MVP 2's HL7 v2
    /// arrives as one more <c>AddSingleton</c> here.
    /// </summary>
    public static IServiceCollection AddReportRendering(this IServiceCollection services)
    {
        // QuestPDF refuses to render until a licence is declared. Community is the correct
        // declaration below its revenue threshold; revisit alongside the licence review before
        // this ships to a paying-scale deployment.
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

        // Singletons: every renderer is stateless.
        services.AddSingleton<IReportRenderer, PdfReportRenderer>();
        services.AddSingleton<IReportRenderer, CsvReportRenderer>();
        services.AddSingleton<IReportRenderer, FhirR4ReportRenderer>();

        return services;
    }
}
