using System.Globalization;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Domain.Enums;
using CardiTrack.Mobile.Controls;
using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Core.Export;

namespace CardiTrack.Mobile.Services;

/// <summary>
/// Journals-only export from CardiJournal: consent pop-ups, generate, then the
/// OS share sheet. Does not open M1-17.
/// </summary>
public interface IJournalExportFlow
{
    /// <summary>
    /// All finished books of <paramref name="audience"/> in the window, or one
    /// entry when <paramref name="entryDate"/> is set.
    /// </summary>
    Task RunAsync(
        Guid memberId,
        string memberName,
        DateOnly from,
        DateOnly to,
        DigestAudience audience,
        DateOnly? entryDate,
        UpdatingOverlay busy);

    /// <summary>Abandons an in-flight journal export when its page is actually left.</summary>
    void Cancel();
}

public sealed class JournalExportFlow : IJournalExportFlow
{
    private static readonly TimeSpan GenerationCeiling = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private const string PdfChoice = "PDF report";
    private const string CsvChoice = "CSV spreadsheet";

    private readonly ICardiTrackApiClient _api;
    private readonly IPopupService _popups;
    private readonly IExportConsentFlow _consent;
    private bool _running;
    private CancellationTokenSource? _run;

    public JournalExportFlow(
        ICardiTrackApiClient api,
        IPopupService popups,
        IExportConsentFlow consent)
    {
        _api = api;
        _popups = popups;
        _consent = consent;
    }

    public void Cancel() => _run?.Cancel();

    public async Task RunAsync(
        Guid memberId,
        string memberName,
        DateOnly from,
        DateOnly to,
        DigestAudience audience,
        DateOnly? entryDate,
        UpdatingOverlay busy)
    {
        if (memberId == Guid.Empty || _running)
            return;
        _running = true;
        _run?.Cancel();
        _run = new CancellationTokenSource();
        var ct = _run.Token;

        try
        {
            var formatChoice = await _popups.ChooseAsync(
                "How should this copy look?",
                "Cancel",
                PdfChoice,
                CsvChoice);
            if (formatChoice is null || ct.IsCancellationRequested)
                return;

            var format = formatChoice == CsvChoice ? ReportFormat.Csv : ReportFormat.Pdf;
            var title = entryDate is { } day
                ? $"{memberName} — {audience}, {day.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}"
                : $"{memberName} — {audience}s";

            var snapshot = JournalExportRequests.Generate(
                memberId, title, from, to, format, audience, entryDate, consentToken: "");
            var consent = await _consent.ConfirmAsync(snapshot, ct);
            if (consent is null || ct.IsCancellationRequested)
                return;

            DiscardCachedExports();
            await busy.ShowUntilHiddenAsync(
                consent.Reused
                    ? "Using your earlier confirmation…"
                    : format == ReportFormat.Pdf
                        ? "We're writing the summary…"
                        : "Preparing your export…");

            try
            {
                var request = JournalExportRequests.Generate(
                    memberId, title, from, to, format, audience, entryDate, consent.Token);
                var queued = await _api.GenerateReportAsync(request, ct);
                var status = await PollUntilReadyAsync(queued.ReportId, ct);

                if (status is null || status.Status != ReportStatus.Ready)
                {
                    busy.Hide();
                    if (!ct.IsCancellationRequested)
                    {
                        await _popups.ShowErrorAsync(
                            status?.Error ?? "We couldn't finish that export. Please try again.",
                            "Couldn't export");
                    }

                    return;
                }

                var file = await _api.DownloadReportAsync(queued.ReportId, ct);
                var path = await WriteToCacheAsync(file, ct);
                busy.Hide();
                await OfferDeliveryAsync(file, path);
            }
            catch (OperationCanceledException)
            {
                busy.Hide();
            }
            catch (ApiException ex)
            {
                busy.Hide();
                if (!ct.IsCancellationRequested)
                    await _popups.ShowErrorAsync(ex.Message, "Couldn't export");
            }
            finally
            {
                busy.Hide();
            }
        }
        finally
        {
            _running = false;
        }
    }

    private async Task<ReportStatusResponse?> PollUntilReadyAsync(
        string reportId, CancellationToken ct)
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

    private async Task OfferDeliveryAsync(ReportFile file, string path)
    {
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

    private static async Task<string> WriteToCacheAsync(ReportFile file, CancellationToken ct)
    {
        var path = Path.Combine(FileSystem.CacheDirectory, file.FileName);
        await File.WriteAllBytesAsync(path, file.Content, ct);
        return path;
    }

    private static void DiscardCachedExports()
    {
        try
        {
            foreach (var path in Directory.EnumerateFiles(
                         FileSystem.CacheDirectory, "carditrack-export-*"))
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
}
