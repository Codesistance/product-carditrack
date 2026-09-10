using System.Globalization;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Reports;
using CardiTrack.Domain.Enums;
using CardiTrack.Mobile.Controls;
using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Core.Auth;
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
}

public sealed class JournalExportFlow : IJournalExportFlow
{
    private static readonly TimeSpan GenerationCeiling = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private const string PdfChoice = "PDF report";
    private const string CsvChoice = "CSV spreadsheet";

    private readonly ICardiTrackApiClient _api;
    private readonly IPopupService _popups;
    private readonly IAuthService _auth;
    private readonly IDeviceBiometric _biometric;
    private bool _running;

    public JournalExportFlow(
        ICardiTrackApiClient api,
        IPopupService popups,
        IAuthService auth,
        IDeviceBiometric biometric)
    {
        _api = api;
        _popups = popups;
        _auth = auth;
        _biometric = biometric;
    }

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

        try
        {
            var formatChoice = await _popups.ChooseAsync(
                "How should this copy look?",
                "Cancel",
                PdfChoice,
                CsvChoice);
            if (formatChoice is null)
                return;

            var format = formatChoice == CsvChoice ? ReportFormat.Csv : ReportFormat.Pdf;
            var title = entryDate is { } day
                ? $"{memberName} — {audience}, {day.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}"
                : $"{memberName} — {audience}s";

            var consent = await ConfirmAsync(
                memberId, from, to, format, audience, entryDate);
            if (consent is null)
                return;

            DiscardCachedExports();
            await busy.ShowUntilHiddenAsync(
                format == ReportFormat.Pdf
                    ? "We're writing the summary…"
                    : "Preparing your export…");

            using var cts = new CancellationTokenSource();
            try
            {
                var request = JournalExportRequests.Generate(
                    memberId, title, from, to, format, audience, entryDate, consent);
                var queued = await _api.GenerateReportAsync(request, cts.Token);
                var status = await PollUntilReadyAsync(queued.ReportId, cts.Token);

                if (status is null || status.Status != ReportStatus.Ready)
                {
                    busy.Hide();
                    await _popups.ShowErrorAsync(
                        status?.Error ?? "We couldn't finish that export. Please try again.",
                        "Couldn't export");
                    return;
                }

                var file = await _api.DownloadReportAsync(queued.ReportId, cts.Token);
                var path = await WriteToCacheAsync(file, cts.Token);
                busy.Hide();
                await OfferDeliveryAsync(file, path);
            }
            catch (OperationCanceledException) when (cts.Token.IsCancellationRequested)
            {
                // We cancelled this wait — the caregiver left, or a newer export replaced it.
            }
            catch (OperationCanceledException)
            {
                busy.Hide();
                await _popups.ShowErrorAsync(
                    "We couldn't finish that export. Please try again.",
                    "Couldn't export");
            }
            catch (ApiException ex)
            {
                busy.Hide();
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

    private async Task<string?> ConfirmAsync(
        Guid memberId,
        DateOnly from,
        DateOnly to,
        ReportFormat format,
        DigestAudience audience,
        DateOnly? entryDate)
    {
        var accepted = await _popups.ConfirmWarningAsync(
            ExportConsentPolicy.Text,
            ExportConsentPolicy.Title,
            ExportConsentPolicy.ConfirmPrompt,
            "Not now");
        if (!accepted)
            return null;

        var method = await ChooseStepUpAsync();
        if (method is null)
            return null;

        if (method == ExportConsentMethod.Password)
        {
            var password = await _popups.AskPasswordAsync(
                "Confirm it's you",
                "Enter the password you use to sign in to CardiTrack.");
            if (password is null)
                return null;

            if (!await _auth.VerifyPasswordAsync(password))
            {
                await _popups.ShowErrorAsync(
                    "That password didn't match. Try again, or use this device's fingerprint or face unlock.",
                    "Couldn't confirm");
                return null;
            }
        }
        else if (!await _biometric.AuthenticateAsync("Confirm this export"))
        {
            await _popups.ShowErrorAsync(
                "We couldn't confirm with fingerprint or face unlock. Try your password instead.",
                "Couldn't confirm");
            return null;
        }

        try
        {
            var recorded = await _api.RecordExportConsentAsync(
                JournalExportRequests.Consent(
                    memberId, from, to, format, audience, entryDate, method.Value));
            return recorded.ConsentToken;
        }
        catch (ApiException ex)
        {
            await _popups.ShowErrorAsync(ex.Message, "Couldn't confirm");
            return null;
        }
    }

    private async Task<ExportConsentMethod?> ChooseStepUpAsync()
    {
        if (_biometric.IsAvailable)
        {
            var choice = await _popups.ChooseAsync(
                "How do you want to confirm?",
                "Cancel",
                "Password",
                "Fingerprint or face unlock");
            return choice switch
            {
                "Password" => ExportConsentMethod.Password,
                "Fingerprint or face unlock" => ExportConsentMethod.Biometric,
                _ => null
            };
        }

        var usePassword = await _popups.ConfirmInfoAsync(
            "Enter the password you use to sign in. This device has no fingerprint or face unlock set up.",
            "Confirm with your password",
            "Continue",
            "Cancel");
        return usePassword ? ExportConsentMethod.Password : null;
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
