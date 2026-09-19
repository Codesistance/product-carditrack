using System.Globalization;
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
    private readonly ICardiTrackApiClient _api;
    private readonly IPopupService _popups;
    private readonly IExportConsentFlow _consent;
    private readonly IExportFileDelivery _delivery;
    private bool _running;
    private CancellationTokenSource? _run;

    public JournalExportFlow(
        ICardiTrackApiClient api,
        IPopupService popups,
        IExportConsentFlow consent,
        IExportFileDelivery delivery)
    {
        _api = api;
        _popups = popups;
        _consent = consent;
        _delivery = delivery;
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
            // Two tiles rather than two rows of text: the formats are recognised by their own
            // glyphs long before their names are read.
            var chosen = await _popups.ChooseExportFormatAsync();
            if (chosen is not { } format || ct.IsCancellationRequested)
                return;
            var title = entryDate is { } day
                ? $"{memberName} — {audience}, {day.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}"
                : $"{memberName} — {audience}s";

            var snapshot = JournalExportRequests.Generate(
                memberId, title, from, to, format, audience, entryDate, consentToken: "");
            var consent = await _consent.ConfirmAsync(snapshot, ct);
            if (consent is null || ct.IsCancellationRequested)
                return;

            _delivery.DiscardCached();
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
                var collected = await _delivery.CollectAsync(queued.ReportId, ct);

                if (collected.File is not { } file)
                {
                    busy.Hide();
                    if (!ct.IsCancellationRequested)
                        await _popups.ShowErrorAsync(collected.Failure!, "Couldn't export");

                    return;
                }

                busy.Hide();
                await _delivery.OfferAsync(file, ct);
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
}
