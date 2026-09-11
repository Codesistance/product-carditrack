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

    public ExportConsentFlow(
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

    public async Task<ExportConsentOutcome?> ConfirmAsync(
        GenerateReportRequest snapshot, CancellationToken ct = default)
    {
        var reused = await TryReuseAsync(snapshot, ct);
        if (reused is not null)
            return reused;

        return await RecordFreshAsync(snapshot, ct);
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
            // A history lookup must not block a fresh confirmation.
            return null;
        }

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
        if (!keep)
            return await RecordFreshAsync(snapshot, ct);

        try
        {
            var recorded = await _api.ReuseExportConsentAsync(snapshot, ct);
            return new ExportConsentOutcome(recorded.ConsentToken, Reused: true);
        }
        catch (ApiException ex) when (ex.IsNotFound)
        {
            return await RecordFreshAsync(snapshot, ct);
        }
        catch (ApiException ex)
        {
            await _popups.ShowErrorAsync(ex.Message, "Couldn't confirm");
            return null;
        }
    }

    private async Task<ExportConsentOutcome?> RecordFreshAsync(
        GenerateReportRequest snapshot, CancellationToken ct)
    {
        var preferBiometric = await OfferBiometricsAsync();

        var accepted = await _popups.ConfirmWarningAsync(
            ExportConsentPolicy.Text,
            ExportConsentPolicy.Title,
            ExportConsentPolicy.ConfirmPrompt,
            "Not now");
        if (!accepted)
            return null;

        var rememberFor = await ChooseRememberForAsync();
        if (rememberFor is null)
            return null;

        var method = await ProveAsync(preferBiometric);
        if (method is null)
            return null;

        try
        {
            var recorded = await _api.RecordExportConsentAsync(
                ToConsent(snapshot, method.Value, rememberFor.Value), ct);
            return new ExportConsentOutcome(recorded.ConsentToken, Reused: false);
        }
        catch (ApiException ex)
        {
            await _popups.ShowErrorAsync(ex.Message, "Couldn't confirm");
            return null;
        }
    }

    /// <summary>
    /// When the device can do fingerprint or face unlock, ask to use it — or
    /// to turn it on — before the responsibility prompt.
    /// </summary>
    private async Task<bool> OfferBiometricsAsync()
    {
        if (_biometric.IsAvailable)
        {
            return await _popups.ConfirmInfoAsync(
                "This device can confirm with fingerprint or face unlock. Use it for this export?",
                "Use fingerprint or face unlock?",
                "Use it",
                "Use my password");
        }

        if (!_biometric.CanEnroll)
            return false;

        var open = await _popups.ConfirmInfoAsync(
            "Turn on fingerprint or face unlock on this device to confirm exports more easily.",
            "Turn on fingerprint or face unlock?",
            "Open settings",
            "Not now");
        if (!open)
            return false;

        await _biometric.OpenEnrollmentSettingsAsync();
        return _biometric.IsAvailable;
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

    private async Task<ExportConsentMethod?> ProveAsync(bool preferBiometric)
    {
        if (preferBiometric && _biometric.IsAvailable)
        {
            if (await _biometric.AuthenticateAsync("Confirm this export"))
                return ExportConsentMethod.Biometric;

            await _popups.ShowErrorAsync(
                "We couldn't confirm with fingerprint or face unlock. Try your password instead.",
                "Couldn't confirm");
            return null;
        }

        if (_biometric.IsAvailable)
        {
            var choice = await _popups.ChooseAsync(
                "How do you want to confirm?",
                "Cancel",
                "Password",
                "Fingerprint or face unlock");
            return choice switch
            {
                "Password" => await ProvePasswordAsync(),
                "Fingerprint or face unlock" => await ProveBiometricAsync(),
                _ => null
            };
        }

        var usePassword = await _popups.ConfirmInfoAsync(
            "Enter the password you use to sign in. This device has no fingerprint or face unlock set up.",
            "Confirm with your password",
            "Continue",
            "Cancel");
        return usePassword ? await ProvePasswordAsync() : null;
    }

    private async Task<ExportConsentMethod?> ProvePasswordAsync()
    {
        var password = await _popups.AskPasswordAsync(
            "Confirm it's you",
            "Enter the password you use to sign in to CardiTrack.");
        if (password is null)
            return null;

        if (await _auth.VerifyPasswordAsync(password))
            return ExportConsentMethod.Password;

        await _popups.ShowErrorAsync(
            "That password didn't match. Try again, or use this device's fingerprint or face unlock.",
            "Couldn't confirm");
        return null;
    }

    private async Task<ExportConsentMethod?> ProveBiometricAsync()
    {
        if (await _biometric.AuthenticateAsync("Confirm this export"))
            return ExportConsentMethod.Biometric;

        await _popups.ShowErrorAsync(
            "We couldn't confirm with fingerprint or face unlock. Try your password instead.",
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
