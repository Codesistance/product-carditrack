using System.Globalization;
using CardiTrack.Domain.Enums;
using CardiTrack.Mobile.Controls;
using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Core.Export;

namespace CardiTrack.Mobile.Services;

/// <summary>
/// Exports one chat conversation: format choice, the responsibility confirmation every export
/// needs, generate, then the OS share sheet.
/// </summary>
/// <remarks>
/// The same shape as <see cref="IJournalExportFlow"/> and sharing everything after the generate
/// call with it (<see cref="IExportFileDelivery"/>). What differs is what is being confirmed: a
/// transcript is the caregiver's own questions and the assistant's answers about a named person,
/// and the consent snapshot names the conversation, so a confirmation given for one thread cannot
/// generate another.
/// </remarks>
public interface IChatTranscriptExportFlow
{
    /// <summary>
    /// The conversation <paramref name="sessionId"/>, as it stands now.
    /// </summary>
    /// <param name="started">and <paramref name="lastTurn"/>: the days the conversation spans, in
    /// the caregiver's own time zone — what the document is dated by and what they confirmed.</param>
    Task RunAsync(
        Guid memberId,
        string memberName,
        Guid sessionId,
        string? label,
        DateOnly started,
        DateOnly lastTurn,
        UpdatingOverlay busy);

    /// <summary>Abandons an in-flight transcript export when its sheet is actually closed.</summary>
    void Cancel();
}

public sealed class ChatTranscriptExportFlow : IChatTranscriptExportFlow
{
    /// <summary>How much of a generated conversation label the document title carries. A theme is
    /// three to six words; the fallback is the opening question, which is not.</summary>
    private const int MaxLabelLength = 60;

    private readonly ICardiTrackApiClient _api;
    private readonly IPopupService _popups;
    private readonly IExportConsentFlow _consent;
    private readonly IExportFileDelivery _delivery;
    private bool _running;
    private CancellationTokenSource? _run;

    public ChatTranscriptExportFlow(
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
        Guid sessionId,
        string? label,
        DateOnly started,
        DateOnly lastTurn,
        UpdatingOverlay busy)
    {
        if (memberId == Guid.Empty || sessionId == Guid.Empty || _running)
            return;
        _running = true;
        _run?.Cancel();
        _run = new CancellationTokenSource();
        var ct = _run.Token;

        try
        {
            var chosen = await _popups.ChooseExportFormatAsync();
            if (chosen is not { } format || ct.IsCancellationRequested)
                return;

            var (from, to) = ChatTranscriptExportRequests.Window(started, lastTurn);
            var title = Title(memberName, label, from);

            var snapshot = ChatTranscriptExportRequests.Generate(
                memberId, sessionId, title, from, to, format, consentToken: "");
            var consent = await _consent.ConfirmAsync(snapshot, ct);
            if (consent is null || ct.IsCancellationRequested)
                return;

            _delivery.DiscardCached();
            await busy.ShowUntilHiddenAsync(
                consent.Reused
                    ? "Using your earlier confirmation…"
                    : "Copying the conversation…");

            try
            {
                var request = ChatTranscriptExportRequests.Generate(
                    memberId, sessionId, title, from, to, format, consent.Token);
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

    /// <summary>
    /// What the document is headed with: who it is about, that it is a chat, and when. The
    /// conversation's own label joins it when there is room — the history list's rows are titled
    /// that way, so the file a caregiver saves is recognisable as the row they exported.
    /// </summary>
    private static string Title(string memberName, string? label, DateOnly on)
    {
        var day = on.ToString("d MMM yyyy", CultureInfo.InvariantCulture);

        var trimmed = label?.Trim();
        if (trimmed is { Length: > MaxLabelLength })
            trimmed = trimmed[..MaxLabelLength].TrimEnd() + "…";
        var subject = trimmed is { Length: > 0 } ? trimmed : "Chat";

        // The name is the launcher's, and the chat sheet is reached from places that have only a
        // first name — or, from the dashboard's bot button, none at all. A title with a dangling
        // dash in front of it is worse than one that simply does not name them; the document
        // itself says who it is about, under its own heading.
        return string.IsNullOrWhiteSpace(memberName)
            ? $"{subject}, {day}"
            : $"{memberName} — {subject}, {day}";
    }
}
