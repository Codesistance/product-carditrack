using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Domain.Enums;
using CardiTrack.Mobile.Controls;
using CardiTrack.Mobile.Core.Api;

namespace CardiTrack.Mobile.Services;

/// <summary>The downloaded export, or why there isn't one. Exactly one of the two is set.</summary>
public sealed record ExportCollected(ReportFile? File, string? Failure);

/// <summary>
/// The half of an export that is the same whatever was exported: wait for the queued report,
/// download it, and put it where the caregiver asked — kept on this phone, or handed to another
/// app.
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

    /// <summary>
    /// Asks the caregiver where the finished file should go and puts it there. Dismissing the
    /// question leaves the file in the cache, which the next export sweeps.
    /// </summary>
    Task OfferAsync(ReportFile file, CancellationToken ct);

    /// <summary>
    /// Keeps the file on this phone and tells the caregiver where it landed, falling back to
    /// <see cref="ShareAsync"/> on their say-so if it could not be kept. For a surface that asks
    /// the question in its own layout rather than through <see cref="OfferAsync"/>'s popup.
    /// </summary>
    Task SaveAsync(ReportFile file, CancellationToken ct);

    /// <summary>Hands the file to the system share sheet.</summary>
    Task ShareAsync(ReportFile file, CancellationToken ct);

    /// <summary>
    /// Opens the file in whatever app on this device claims its type, putting it nowhere. Says so
    /// plainly when nothing does — likelier for a FHIR bundle than for a PDF.
    /// </summary>
    Task OpenAsync(ReportFile file, CancellationToken ct);

    /// <summary>
    /// Whether this platform can keep a file somewhere the caregiver would find it again. False
    /// means a surface should not offer Save at all — see <c>IExportFileSaver</c>.
    /// </summary>
    bool CanSave { get; }

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
    private readonly IExportFileSaver _saver;

    public ExportFileDelivery(ICardiTrackApiClient api, IPopupService popups, IExportFileSaver saver)
    {
        _api = api;
        _popups = popups;
        _saver = saver;
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

    public bool CanSave => _saver.IsSupported;

    public async Task OfferAsync(ReportFile file, CancellationToken ct)
    {
        var chosen = await _popups.ChooseExportDeliveryAsync(
            file.FileName, _saver.IsSupported ? _saver.PlaceName : null);
        if (chosen is null || ct.IsCancellationRequested)
            return;

        switch (chosen)
        {
            case ExportDelivery.Share:
                await ShareAsync(file, ct);
                break;
            case ExportDelivery.Open:
                await OpenAsync(file, ct);
                break;
            default:
                await SaveAsync(file, ct);
                break;
        }
    }

    public async Task SaveAsync(ReportFile file, CancellationToken ct)
    {
        var saved = await _saver.SaveAsync(file, ct);
        if (ct.IsCancellationRequested)
            return;

        if (saved is { Ok: true, Where: { } where })
        {
            await _popups.ShowInfoAsync($"{file.FileName} is in {where}.", "Saved to this phone");
            return;
        }

        // The save failed — a full disk, a revoked permission, a platform that moved. The
        // caregiver asked for this file, so the sheet is offered rather than the export being
        // lost to an apology: it reaches the same Files app by a longer road.
        var share = await _popups.ConfirmWarningAsync(
            "We couldn't keep it on this phone. You can still send it somewhere from here.",
            "Couldn't save it",
            "Share instead",
            "Not now");
        if (share && !ct.IsCancellationRequested)
            await ShareAsync(file, ct);
    }

    public async Task ShareAsync(ReportFile file, CancellationToken ct)
    {
        // The sheet takes a file, so the bytes have to be somewhere it can reach. The cache is
        // that somewhere, and the next export sweeps it: a named health record must not sit in
        // app storage longer than the share it was written for.
        var path = await WriteToCacheAsync(file, ct);
        if (ct.IsCancellationRequested)
            return;

        await Share.Default.RequestAsync(new ShareFileRequest
        {
            Title = file.FileName,
            File = new ShareFile(path)
        });
    }

    public async Task OpenAsync(ReportFile file, CancellationToken ct)
    {
        // Same cache copy the share sheet needs, for the same reason: the launcher takes a file,
        // not bytes. Nothing is kept by opening — the next export sweeps it.
        var path = await WriteToCacheAsync(file, ct);
        if (ct.IsCancellationRequested)
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
                "There's no app on this device that opens this kind of file. Try Save or Share instead.",
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
