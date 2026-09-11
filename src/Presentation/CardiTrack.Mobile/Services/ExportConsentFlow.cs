using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Reports;
using CardiTrack.Domain.Enums;
using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Core.Auth;
using CardiTrack.Mobile.Core.Export;

namespace CardiTrack.Mobile.Services;

/// <summary>The minted generate token, and whether a standing grant produced it.</summary>
public sealed record ExportConsentOutcome(string Token, bool Reused);

/// <summary>
/// Responsibility popup, how long to keep it, biometric-or-password step-up,
/// and reuse of a standing grant. Shared by M1-17 and CardiJournal export.
/// </summary>
public interface IExportConsentFlow
{
    Task<ExportConsentOutcome?> ConfirmAsync(
        GenerateReportRequest snapshot, CancellationToken ct = default);
}

public sealed class ExportConsentFlow : IExportConsentFlow
{
    private readonly ICardiTrackApiClient _api;
    private readonly IPopupService _popups;
    private readonly IAuthService _auth;
    private readonly IDeviceBiometric _biometric;
    private readonly IAppResumeNotifier _resumes;

    public ExportConsentFlow(
        ICardiTrackApiClient api,
        IPopupService popups,
        IAuthService auth,
        IDeviceBiometric biometric,
        IAppResumeNotifier resumes)
    {
        _api = api;
        _popups = popups;
        _auth = auth;
        _biometric = biometric;
        _resumes = resumes;
    }

    public async Task<ExportConsentOutcome?> ConfirmAsync(
        GenerateReportRequest snapshot, CancellationToken ct = default)
    {
        try
        {
            if (ct.IsCancellationRequested)
                return null;

            var reused = await TryReuseAsync(snapshot, ct);
            if (reused is not null)
                return reused;
            if (ct.IsCancellationRequested)
                return null;

            return await RecordFreshAsync(snapshot, ct);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private async Task<ExportConsentOutcome?> TryReuseAsync(
        GenerateReportRequest snapshot, CancellationToken ct)
    {
        List<ExportConsentHistoryItem> history;
        try
        {
            history = await _api.GetExportConsentsAsync(ct);
        }
        catch (ApiException)
        {
            // A history lookup must not block a fresh confirmation. Caller
            // cancel is also wrapped as ApiException; ConfirmAsync checks
            // ct before starting RecordFreshAsync.
            return null;
        }

        if (ct.IsCancellationRequested)
            return null;

        var grant = history.FirstOrDefault(c => c.CanReuse);
        if (grant is null)
            return null;

        var notice = grant.RememberUntil is { } until
            ? ExportConsentCopy.ReuseNotice(grant.RecordedAt, until)
            : "We're using your earlier confirmation. You can stop this in Settings.";

        var keep = await _popups.ConfirmInfoAsync(
            notice,
            "Using your earlier confirmation",
            "Continue",
            "Confirm again");
        if (ct.IsCancellationRequested)
            return null;
        if (!keep)
            return await RecordFreshAsync(snapshot, ct);

        try
        {
            var recorded = await _api.ReuseExportConsentAsync(grant.Id, snapshot, ct);
            return new ExportConsentOutcome(recorded.ConsentToken, Reused: true);
        }
        catch (ApiException ex) when (ex.IsNotFound)
        {
            if (ct.IsCancellationRequested)
                return null;
            return await RecordFreshAsync(snapshot, ct);
        }
        catch (ApiException ex)
        {
            if (ct.IsCancellationRequested)
                return null;
            await _popups.ShowErrorAsync(ex.Message, "Couldn't confirm");
            return null;
        }
    }

    private async Task<ExportConsentOutcome?> RecordFreshAsync(
        GenerateReportRequest snapshot, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return null;

        var preference = await OfferBiometricsAsync(ct);
        if (ct.IsCancellationRequested)
            return null;

        var accepted = await _popups.ConfirmWarningAsync(
            ExportConsentPolicy.Text,
            ExportConsentPolicy.Title,
            ExportConsentPolicy.ConfirmPrompt,
            "Not now");
        if (!accepted || ct.IsCancellationRequested)
            return null;

        var rememberFor = await ChooseRememberForAsync();
        if (rememberFor is null || ct.IsCancellationRequested)
            return null;

        var method = await ProveAsync(preference, ct);
        if (method is null || ct.IsCancellationRequested)
            return null;

        try
        {
            var recorded = await _api.RecordExportConsentAsync(
                ToConsent(snapshot, method.Value, rememberFor.Value), ct);
            return new ExportConsentOutcome(recorded.ConsentToken, Reused: false);
        }
        catch (ApiException ex)
        {
            if (ct.IsCancellationRequested)
                return null;
            await _popups.ShowErrorAsync(ex.Message, "Couldn't confirm");
            return null;
        }
    }

    private enum ProofPreference
    {
        Password,
        Biometric
    }

    /// <summary>
    /// When the device can do fingerprint or face unlock, ask to use it — or
    /// to turn it on — before the responsibility prompt.
    /// </summary>
    private async Task<ProofPreference> OfferBiometricsAsync(CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return ProofPreference.Password;

        if (_biometric.IsAvailable)
        {
            var useIt = await _popups.ConfirmInfoAsync(
                "This device can confirm with fingerprint or face unlock. Use it for this export?",
                "Use fingerprint or face unlock?",
                "Use it",
                "Use my password");
            return useIt ? ProofPreference.Biometric : ProofPreference.Password;
        }

        if (!_biometric.CanEnroll)
            return ProofPreference.Password;

        var open = await _popups.ConfirmInfoAsync(
            "Turn on fingerprint or face unlock in this device's Settings app, then come back to confirm this export.",
            "Turn on fingerprint or face unlock?",
            "Open settings",
            "Not now");
        if (!open || ct.IsCancellationRequested)
            return ProofPreference.Password;

        if (!await OpenEnrollmentAndWaitAsync(ct) || ct.IsCancellationRequested)
            return ProofPreference.Password;

        var useNow = await _popups.ConfirmInfoAsync(
            "If fingerprint or face unlock is on now, use it for this export. Otherwise we'll use your password.",
            "Use fingerprint or face unlock?",
            "Use it",
            "Use my password");
        return useNow && _biometric.IsAvailable
            ? ProofPreference.Biometric
            : ProofPreference.Password;
    }

    /// <summary>
    /// Settings' StartActivity/OpenUrl returns as soon as the OS screen launches.
    /// Wait for the app to resume before asking whether to use biometrics, otherwise
    /// the follow-up popup opens over Settings and IsAvailable is still false.
    /// A timeout that then continued the flow would still open popups over Settings;
    /// leave waits until resume or the page-lifetime token is cancelled.
    /// </summary>
    private async Task<bool> OpenEnrollmentAndWaitAsync(CancellationToken ct)
    {
        var resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnResumed(object? sender, EventArgs e) => resumed.TrySetResult();
        _resumes.Resumed += OnResumed;
        try
        {
            if (!await _biometric.OpenEnrollmentSettingsAsync())
                return false;

            await resumed.Task.WaitAsync(ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            _resumes.Resumed -= OnResumed;
        }
    }

    private async Task<ExportConsentRememberFor?> ChooseRememberForAsync()
    {
        var labels = ExportConsentPolicy.RememberChoices
            .Select(ExportConsentPolicy.RememberChoiceLabel)
            .ToArray();
        var choice = await _popups.ChooseAsync(
            "Keep this confirmation?",
            "Cancel",
            labels);
        if (choice is null)
            return null;

        var index = Array.IndexOf(labels, choice);
        return index >= 0
            ? ExportConsentPolicy.RememberChoices[index]
            : ExportConsentRememberFor.ThisExport;
    }

    private async Task<ExportConsentMethod?> ProveAsync(
        ProofPreference preference, CancellationToken ct)
    {
        if (preference == ProofPreference.Biometric && _biometric.IsAvailable)
        {
            if (await _biometric.AuthenticateAsync("Confirm this export"))
                return ExportConsentMethod.Biometric;

            if (ct.IsCancellationRequested)
                return null;

            await _popups.ShowErrorAsync(
                "We couldn't confirm with fingerprint or face unlock. Try your password instead.",
                "Couldn't confirm");
            return await ProvePasswordAsync(ct);
        }

        if (!_biometric.IsAvailable)
        {
            var usePassword = await _popups.ConfirmInfoAsync(
                "Enter the password you use to sign in. This device has no fingerprint or face unlock set up.",
                "Confirm with your password",
                "Continue",
                "Cancel");
            return usePassword ? await ProvePasswordAsync(ct) : null;
        }

        return await ProvePasswordAsync(ct);
    }

    private async Task<ExportConsentMethod?> ProvePasswordAsync(CancellationToken ct)
    {
        var password = await _popups.AskPasswordAsync(
            "Confirm it's you",
            "Enter the password you use to sign in to CardiTrack.");
        if (password is null || ct.IsCancellationRequested)
            return null;

        try
        {
            if (await _auth.VerifyPasswordAsync(password, ct))
                return ExportConsentMethod.Password;
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        if (ct.IsCancellationRequested)
            return null;

        await _popups.ShowErrorAsync(
            "That password didn't match. Try again, or use this device's fingerprint or face unlock.",
            "Couldn't confirm");
        return null;
    }

    private static RecordExportConsentRequest ToConsent(
        GenerateReportRequest snapshot,
        ExportConsentMethod method,
        ExportConsentRememberFor rememberFor) => new()
    {
        CardiMemberIds = snapshot.CardiMemberIds,
        DateRangeFrom = snapshot.DateRangeFrom,
        DateRangeTo = snapshot.DateRangeTo,
        Format = snapshot.Format,
        IncludeMetrics = snapshot.IncludeMetrics,
        IncludeTrends = snapshot.IncludeTrends,
        IncludeAlerts = snapshot.IncludeAlerts,
        IncludeJournals = snapshot.IncludeJournals,
        IncludeNotices = snapshot.IncludeNotices,
        IncludeDevices = snapshot.IncludeDevices,
        JournalEntryDate = snapshot.JournalEntryDate,
        JournalAudience = snapshot.JournalAudience,
        Method = method,
        RememberFor = rememberFor,
        AcceptedResponsibility = true
    };
}
