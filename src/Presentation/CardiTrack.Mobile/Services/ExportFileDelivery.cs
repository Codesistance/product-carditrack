using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Domain.Enums;
using CardiTrack.Mobile.Core.Api;

namespace CardiTrack.Mobile.Services;

/// <summary>The downloaded export, or why there isn't one. Exactly one of the two is set.</summary>
public sealed record ExportCollected(ReportFile? File, string? Failure);

/// <summary>
/// The half of an export that is the same whatever was exported: wait for the queued report,
/// download it, put it in the cache directory, and hand it to the OS.
/// </summary>
/// <remarks>
/// Split out when the chat transcript became the second thing a caregiver can export. The consent
/// step differs between them — what is being confirmed is different — but everything after the
/// generate call is identical, and two copies of a polling loop drift in exactly the way that
/// leaves one of them writing files nothing sweeps up.
/// </remarks>
public interface IExportFileDelivery
{
    /// <summary>
    /// Polls until the report is ready and downloads it, or comes back with what to tell the
    /// caregiver when it failed, expired or the wait ran out.
    /// </summary>
    Task<ExportCollected> CollectAsync(string reportId, CancellationToken ct);

    /// <summary>Writes the file where the share sheet can reach it and offers it to the OS.</summary>
    Task OfferAsync(ReportFile file, CancellationToken ct);

    /// <summary>
    /// Drops earlier exports from the cache before a new one is written. A named health record
    /// must not sit in a cache directory any longer than the share it was written for.
    /// </summary>
    void DiscardCached();
}

public sealed class ExportFileDelivery : IExportFileDelivery
{
    /// <summary>How long a generation may run before the wait is abandoned. Generous: the file is
    /// a document someone plans to take to an appointment, not a screen refresh.</summary>
    private static readonly TimeSpan GenerationCeiling = TimeSpan.FromMinutes(3);

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private const string CachePrefix = "carditrack-";

    private readonly ICardiTrackApiClient _api;
    private readonly IPopupService _popups;

    public ExportFileDelivery(ICardiTrackApiClient api, IPopupService popups)
    {
        _api = api;
        _popups = popups;
    }

    public async Task<ExportCollected> CollectAsync(string reportId, CancellationToken ct)
    {
        var status = await PollUntilReadyAsync(reportId, ct);
        if (status is null || status.Status != ReportStatus.Ready)
        {
            return new ExportCollected(
                null, status?.Error ?? "We couldn't finish that export. Please try again.");
        }

        return new ExportCollected(await _api.DownloadReportAsync(reportId, ct), null);
    }

    public async Task OfferAsync(ReportFile file, CancellationToken ct)
    {
        var path = await WriteToCacheAsync(file, ct);
        if (ct.IsCancellationRequested)
            return;

        var choice = await _popups.ChooseAsync(
            $"{file.FileName}",
            "Close",
            "Save or share",
            "Open");

        if (choice == "Save or share")
        {
            await Share.Default.RequestAsync(new ShareFileRequest
            {
                Title = "Save or share export",
                File = new ShareFile(path)
            });
            return;
        }

        if (choice != "Open")
            return;

        try
        {
            await Launcher.Default.OpenAsync(new OpenFileRequest
            {
                Title = file.FileName,
                File = new ReadOnlyFile(path)
            });
        }
        catch (Exception)
        {
            await _popups.ShowInfoAsync(
                "There's no app on this device that opens this kind of file. Try \"Save or share\" instead.",
                "Can't open it here");
        }
    }

    public void DiscardCached()
    {
        try
        {
            foreach (var path in Directory.EnumerateFiles(
                         FileSystem.CacheDirectory, CachePrefix + "*"))
            {
                try
                {
                    File.Delete(path);
                }
                catch (Exception)
                {
                    // One undeletable file must not stop the sweep clearing the rest.
                }
            }
        }
        catch (Exception)
        {
            // No cache directory yet, or it is unreadable — nothing to clean either way.
        }
    }

    private async Task<ReportStatusResponse?> PollUntilReadyAsync(string reportId, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + GenerationCeiling;
        ReportStatusResponse? last = null;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            last = await _api.GetReportStatusAsync(reportId, ct);
            if (last is not null && last.Status != ReportStatus.Pending)
                return last;
            await Task.Delay(PollInterval, ct);
        }

        return last;
    }

    private static async Task<string> WriteToCacheAsync(ReportFile file, CancellationToken ct)
    {
        var path = Path.Combine(FileSystem.CacheDirectory, file.FileName);
        await File.WriteAllBytesAsync(path, file.Content, ct);
        return path;
    }
}
