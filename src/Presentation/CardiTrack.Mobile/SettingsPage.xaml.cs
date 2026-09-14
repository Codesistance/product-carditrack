using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Core.Auth;
using CardiTrack.Mobile.Core.Onboarding;
using CardiTrack.Mobile.Onboarding;
using CardiTrack.Mobile.Services;
using Serilog;
// CardiTrack.Application (the DTO assembly's root namespace) shadows MAUI's Application in
// any file importing it, so the control type is aliased rather than qualified at each use.
using MauiApplication = Microsoft.Maui.Controls.Application;

namespace CardiTrack.Mobile;

public partial class SettingsPage : ContentPage
{
    private readonly IAuthService _authService;
    private readonly IPopupService _popups;
    private readonly CardiMemberDraftStore _drafts;
    private readonly ICardiTrackApiClient _api;
    private readonly IDeviceBiometric _biometric;

    // A Switch raises Toggled when set from code too; this keeps OnAppearing from writing the
    // preference back and re-announcing a consent the caregiver did not just change.
    private bool _renderingDiagnostics;

    public SettingsPage(
        IAuthService authService,
        IPopupService popups,
        CardiMemberDraftStore drafts,
        ICardiTrackApiClient api)
    {
        InitializeComponent();
        _authService = authService;
        _popups = popups;
        _drafts = drafts;
        _api = api;
        _biometric = ServiceHelper.GetRequiredService<IDeviceBiometric>();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        AccountNameLabel.Text = _authService.CurrentUserName ?? "Your account";
        AccountEmailLabel.Text = _authService.CurrentUserEmail ?? string.Empty;
        VersionLabel.Text = $"{AppInfo.Current.VersionString} ({AppInfo.Current.BuildString})";
        RenderDiagnosticsConsent();
        _ = LoadMutesAsync();
        _ = LoadNotificationSummaryAsync();
    }

    /// <summary>
    /// The row's one line says what is set, so the page answers before it is tapped. The device's
    /// saved preferences write it at once and the live ones correct it behind — silently, with no
    /// banner or overlay: this is a subtitle echoing the caregiver's own settings, not health data
    /// standing in for something current, and a scrim over the whole of Settings for one line
    /// would be exactly the flash the saved copy exists to remove.
    /// </summary>
    private async Task LoadNotificationSummaryAsync()
    {
        // Tracked for this run rather than read off the label: Settings is a tab, so the label
        // may still hold the line a previous visit wrote. Asking whether it is empty would let
        // that stale line survive a run in which nothing was read at all.
        var wrote = false;
        try
        {
            if (await _api.PeekNotificationPreferencesAsync() is { } saved)
            {
                ApplyNotificationSummary(saved);
                wrote = true;
            }

            ApplyNotificationSummary(await _api.GetNotificationPreferencesAsync());
        }
        catch (ApiException)
        {
            // Only when this run wrote nothing: a line from the device beats the generic one.
            if (!wrote)
                NotificationSummary.Text = "Quiet hours, lock-screen detail, what to hear about";
        }
    }

    private void ApplyNotificationSummary(NotificationPreferenceResponse prefs)
    {
        var quiet = prefs.QuietHoursStart is { } start && prefs.QuietHoursEnd is { } end
            ? $"Quiet {start:HH:mm} – {end:HH:mm}"
            : "No quiet hours";
        // Safety cannot be muted — the API strips it on every update — so a stored list that
        // still names it (from before that rule) must not count as a kind muted here.
        var mutedCount = prefs.MutedCategories.Count(c =>
            !string.Equals(c, nameof(CardiTrack.Domain.Enums.NotificationCategory.Safety), StringComparison.OrdinalIgnoreCase));
        var muted = mutedCount switch
        {
            0 => "hearing about everything",
            1 => "1 kind muted",
            var n => $"{n} kinds muted",
        };
        NotificationSummary.Text = $"{quiet} · {muted}";
    }

    private async void OnNotificationPreferencesTapped(object? sender, TappedEventArgs e) =>
        await Shell.Current.GoToAsync(NotificationPreferencesPage.Route);

    // The same reset-link call the signed-out Forgot Password screen makes, sent to the
    // signed-in address without asking for it again — there is nothing else to type.
    /// <summary>M1-17 Health Data Export, unscoped — the page asks which member.</summary>
    private async void OnExportHealthDataTapped(object? sender, TappedEventArgs e) =>
        await Shell.Current.GoToAsync(ExportHealthDataPage.Route);

    private async void OnExportConsentsTapped(object? sender, TappedEventArgs e) =>
        await Shell.Current.GoToAsync(ExportConsentsPage.Route);

    private async void OnChangePasswordTapped(object? sender, TappedEventArgs e)
    {
        var email = _authService.CurrentUserEmail;
        if (string.IsNullOrWhiteSpace(email))
        {
            await _popups.ShowWarningAsync("We don't have an email address for this account.", "Can't send a link");
            return;
        }

        var send = await _popups.ConfirmInfoAsync(
            $"We'll email a link to {email}. Follow it to set a new password.",
            "Change password", "Send link", "Not now");
        if (!send)
            return;

        try
        {
            await _authService.RequestPasswordResetAsync(email);
            ChangePasswordDetail.Text = $"Link sent to {email}";
        }
        catch (CardiTrack.Mobile.Core.Auth.AuthException ex)
        {
            await _popups.ShowWarningAsync(ex.Message, "Couldn't send the link");
        }
    }

    private async void OnTermsTapped(object? sender, TappedEventArgs e) =>
        await Navigation.PushModalAsync(new LegalDocumentPage(LegalDocumentPage.TermsTitle, LegalDocumentPage.TermsUrl));

    private async void OnPrivacyTapped(object? sender, TappedEventArgs e) =>
        await Navigation.PushModalAsync(new LegalDocumentPage(LegalDocumentPage.PrivacyTitle, LegalDocumentPage.PrivacyUrl));

    // Starts the erasure request the privacy policy promises (30 days), the same way the
    // policy page itself does — a pre-addressed email — until an endpoint exists.
    // The tick box is the confirmation: the card above it has already said what happens, so
    // the button does not ask again. It stays off — and reads off — until the box is ticked.
    private void OnDeleteConfirmChanged(object? sender, CheckedChangedEventArgs e)
    {
        DeleteAccountBtn.IsEnabled = e.Value;
        DeleteAccountBtn.Opacity = e.Value ? 1 : 0.5;
    }

    /// <summary>
    /// Starts an account deletion, in the app, the way Play's account-deletion policy and Apple's
    /// Guideline 5.1.1(v) both require.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three gates, in this order: the tick box says they have read what happens, the device
    /// biometric says the person holding the phone is its owner, and a last dialog states the
    /// consequence the terms of service now spell out — that monitoring stops today, not in thirty
    /// days. The biometric is the same step an export already takes; deleting an account should
    /// not be easier than downloading a PDF.
    /// </para>
    /// <para>
    /// On success the caregiver is signed out, because every other endpoint will now refuse them.
    /// Leaving them on a signed-in session that can load nothing would look like the app breaking
    /// rather than like their request being honoured.
    /// </para>
    /// </remarks>
    private async void OnDeleteAccountClicked(object? sender, EventArgs e)
    {
        if (!DeleteConfirmCheck.IsChecked)
            return;

        if (!await ConfirmItIsThemAsync())
            return;

        // Said last, and said in terms of the person they watch over rather than of the account:
        // "your data will be deleted" is not the sentence that makes someone stop and think.
        var confirmed = await _popups.ConfirmWarningAsync(
            "Monitoring stops now — not in 30 days. Anyone you watch over who has no other "
            + "caregiver will not be monitored, and no alerts will be sent about them.\n\n"
            + "You have 30 days to change your mind: sign in again and cancel. After that "
            + "everything is deleted and cannot be brought back.",
            "Delete this account?",
            confirmText: "Delete my account",
            cancelText: "Keep my account");

        if (!confirmed)
            return;

        DeleteAccountBtn.IsEnabled = false;
        try
        {
            var status = await _api.RequestAccountDeletionAsync();

            // Signed out, then told — in that order, so the message is the last thing on screen
            // rather than something dismissed on the way to a sign-in page.
            await SignOutForDeletionAsync();

            var due = status.ScheduledForUtc?.ToLocalTime();
            await _popups.ShowInfoAsync(
                due is { } when_
                    ? $"Your account will be deleted on {when_:d MMMM yyyy}. Sign in before then to stop it."
                    : "Your account is scheduled for deletion. Sign in within 30 days to stop it.",
                "Request received");
        }
        catch (ApiException ex)
        {
            await _popups.ShowWarningAsync(ex.Message, "Couldn't start the deletion");
        }
        finally
        {
            DeleteAccountBtn.IsEnabled = true;
        }
    }

    /// <summary>
    /// The device's own check that the phone's owner is present. Refuses rather than degrades when
    /// there is nothing enrolled: an account deletion is not a step to wave through because a
    /// caregiver never set up a fingerprint.
    /// </summary>
    private async Task<bool> ConfirmItIsThemAsync()
    {
        if (!_biometric.IsAvailable)
        {
            // Fingerprint or face only — not "a screen lock". DeviceBiometric allows
            // BiometricStrong | BiometricWeak, so a PIN does not satisfy it, and telling a
            // caregiver to set one up would send them away to do something that still gets
            // refused. Worth being exact about: this message is the whole of their next step.
            await _popups.ShowWarningAsync(
                _biometric.CanEnroll
                    ? "Set up a fingerprint or face unlock on this phone first — we ask for it "
                      + "before deleting an account."
                    : "This phone cannot do fingerprint or face unlock, and we ask for one before "
                      + "deleting an account. Email support@carditrack.com and we will do it for you.",
                "We need to check it is you");
            return false;
        }

        return await _biometric.AuthenticateAsync("Confirm it is you before deleting your account");
    }

    /// <summary>
    /// The same clearing as an ordinary sign-out. Deliberately the same code path: an account
    /// awaiting deletion must not leave a draft, a cached reading or a remembered consent behind
    /// on the phone any more than a signed-out one does.
    /// </summary>
    private async Task SignOutForDeletionAsync()
    {
        // Fail-closed, step by step. By the time this runs the server has already accepted the
        // deletion, so there is nothing to roll back and no useful way to report a half-failure:
        // one step throwing must not stop the rest, or a token or a cached reading survives on a
        // phone whose owner has just asked for all of it to go. An ordinary sign-out can afford to
        // surface the failure; this one cannot afford to stop.
        await TryAsync(() => _authService.SignOutAsync(), "sign-out");
        Try(() => Preferences.Default.Remove("PrimaryCardiMemberId"), "primary member");
        Try(() => Preferences.Default.Remove("VerifyEmailNudgeDismissed"), "verify-email nudge");
        Try(() => Preferences.Default.Remove(DashboardPage.HealthDataDisclosureConfirmedKey), "disclosure hint");
        Try(() => Preferences.Default.Remove(WizardLauncher.ResumeDismissedKey), "wizard resume flag");
        Try(DiagnosticsConsent.Clear, "diagnostics consent");
        await TryAsync(() => _drafts.ClearAsync(), "member draft");

        // Always, even if every step above failed: leaving them inside an app that can no longer
        // load anything is the worst of the available outcomes.
        WindowNavigation.SetRootPage(this, new NavigationPage(new SignInPage()));
    }

    private void Try(Action step, string what)
    {
        try
        {
            step();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Deletion sign-out could not clear the {What}.", what);
        }
    }

    private async Task TryAsync(Func<Task> step, string what)
    {
        try
        {
            await step();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Deletion sign-out could not complete the {What}.", what);
        }
    }

    /// <summary>Settings is a tab root reachable by deep link (notification preferences,
    /// timezone), so like Alerts the arrow falls back to the dashboard when there is no history
    /// of its own to pop.</summary>
    private async void OnBackTapped(object? sender, TappedEventArgs e) =>
        await this.GoBackAsync(AppShell.DashboardRoute);

    // ------------------------------------------------------------------ silenced reminders

    /// <summary>
    /// Lists what the user has silenced. The card hides itself when there is nothing muted —
    /// an empty "Silenced reminders" section would imply a feature they have not used.
    /// </summary>
    private async Task LoadMutesAsync()
    {
        var showedSaved = false;
        try
        {
            // The saved list first, so the card is there as the page opens; the live one
            // corrects it behind. Silent, for the same reason the summary line above is.
            if (await _api.PeekNotificationMutesAsync() is { } saved)
            {
                RenderMutes(saved);
                showedSaved = true;
            }

            RenderMutes(await _api.GetNotificationMutesAsync());
        }
        catch (ApiException)
        {
            // Settings must still open if this call fails; the section simply does not appear —
            // unless the device's own list is already showing, which is better than nothing.
            if (!showedSaved)
                MutesCard.IsVisible = false;
        }
    }

    private void RenderMutes(List<NotificationMuteResponse> mutes)
    {
        MutesList.Clear();

        foreach (var mute in mutes)
            MutesList.Add(BuildMuteRow(mute));

        MutesSubtitle.Text = mutes.Count == 1
            ? "1 reminder you've turned off."
            : $"{mutes.Count} reminders you've turned off.";

        MutesCard.IsVisible = mutes.Count > 0;
    }

    private View BuildMuteRow(NotificationMuteResponse mute)
    {
        var label = new Label
        {
            Text = MuteDescription(mute),
            LineBreakMode = LineBreakMode.WordWrap,
            VerticalOptions = LayoutOptions.Center
        };
        if (Resources.TryGetValue("Body2", out var bodyStyle) && bodyStyle is Style style)
            label.Style = style;

        var undo = new Button
        {
            Text = "Turn back on",
            FontFamily = "QuicksandSemiBold",
            FontSize = 13,
            BackgroundColor = Colors.Transparent,
            Padding = new Thickness(8, 0),
            HeightRequest = 36
        };
        if (MauiApplication.Current?.Resources.TryGetValue("Primary", out var primary) == true
            && primary is Color colour)
        {
            undo.TextColor = colour;
        }

        undo.Clicked += async (_, _) => await RemoveMuteAsync(mute.Id);

        var grid = new Grid
        {
            ColumnDefinitions =
            [
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto)
            ],
            ColumnSpacing = 10
        };
        grid.Add(label, 0);
        grid.Add(undo, 1);
        return grid;
    }

    /// <summary>
    /// Describes a mute in the user's terms. Rule codes are an implementation detail, so an
    /// unmapped one degrades to its scope rather than leaking <c>DEVICE_STALE_LONG</c> into the UI.
    /// </summary>
    private static string MuteDescription(NotificationMuteResponse mute)
    {
        var subject = mute.RuleCode switch
        {
            "DEVICE_REMOVED" => "Reminders about a missing wearable",
            "DEVICE_STALE_LONG" => "Reminders when a watch stops syncing",
            "TIMEZONE_DEFAULT" => "The time zone reminder",
            "BASELINE_STALLED" => "Reminders about stalled learning",
            "SLEEP_SCOPE_MISSING" => "Reminders about sleep access",
            "MEDICAL_NOTES_EMPTY" => "Reminders about health background",
            "PAUSE_LEFT_LONG" => "Reminders about long pauses",
            null when mute.Category is not null => $"Everything in {mute.Category}",
            _ => "A reminder"
        };

        return mute.CardiMemberName is { Length: > 0 } name
            ? $"{subject} — {name}"
            : subject;
    }

    private async Task RemoveMuteAsync(Guid muteId)
    {
        try
        {
            await _api.RemoveNotificationMuteAsync(muteId);
            await LoadMutesAsync();
        }
        catch (ApiException ex)
        {
            await _popups.ShowErrorAsync(ex.Message, "That didn't work");
        }
    }

    private async void OnResetMutesClicked(object? sender, EventArgs e)
    {
        var confirmed = await _popups.ConfirmWarningAsync(
            "Every reminder you've turned off will come back if it still applies.",
            "Show everything again?",
            confirmText: "Show them");

        if (!confirmed)
            return;

        ResetMutesBtn.IsEnabled = false;
        try
        {
            await _api.ResetNotificationMutesAsync();
            await LoadMutesAsync();
        }
        catch (ApiException ex)
        {
            await _popups.ShowErrorAsync(ex.Message, "That didn't work");
        }
        finally
        {
            ResetMutesBtn.IsEnabled = true;
        }
    }

    // Sign out asks twice the way leaving the app does: the first tap arms the same two-second
    // window (CardiTrack.Mobile.Core.Navigation.ExitConfirmation) and raises the dashboard's
    // deep-red banner; a second tap inside it signs out. Leaving the tab, or letting the window
    // lapse, forgets the first tap.
    private readonly CardiTrack.Mobile.Core.Navigation.ExitConfirmation _signOutGate = new();
    private CancellationTokenSource? _exitHintCts;

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _signOutGate.Disarm();
        HideExitHint();
    }

    private void ShowExitHint()
    {
        if (!ExitHintBanner.IsVisible)
        {
            ExitHintScrim.Opacity = 0;
            ExitHintBanner.Opacity = 0;
            ExitHintScrim.IsVisible = true;
            ExitHintBanner.IsVisible = true;
            _ = ExitHintScrim.FadeToAsync(1, 140);
            _ = ExitHintBanner.FadeToAsync(1, 140);
        }

        // Every tap re-arms: the previous source is cancelled and disposed, not just replaced.
        var previous = _exitHintCts;
        _exitHintCts = new CancellationTokenSource();
        previous?.Cancel();
        previous?.Dispose();
        _ = HideExitHintAfterAsync(_exitHintCts.Token);
    }

    private async Task HideExitHintAfterAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(CardiTrack.Mobile.Core.Navigation.ExitConfirmation.Window, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        HideExitHint();
    }

    private void HideExitHint()
    {
        var cts = _exitHintCts;
        _exitHintCts = null;
        cts?.Cancel();
        cts?.Dispose();
        ExitHintBanner.IsVisible = false;
        ExitHintScrim.IsVisible = false;
    }

    private void RenderDiagnosticsConsent()
    {
        _renderingDiagnostics = true;
        try
        {
            DiagnosticsSwitch.IsToggled = DiagnosticsConsent.IsGranted;
        }
        finally
        {
            _renderingDiagnostics = false;
        }
    }

    private void OnDiagnosticsToggled(object? sender, ToggledEventArgs e)
    {
        if (_renderingDiagnostics)
            return;
        DiagnosticsConsent.Set(e.Value);
    }

    /// <summary>
    /// Nothing to share is the ordinary case, not a failure: the log file only records Warning
    /// and above, so a phone that has behaved has no file at all. Saying so beats opening a share
    /// sheet over an empty archive, which reads as the feature being broken.
    /// </summary>
    private async void OnShareAppLogsTapped(object? sender, TappedEventArgs e)
    {
        try
        {
            if (!await AppLogShare.TryShareAsync())
                await _popups.ShowInfoAsync(
                    "This phone hasn't recorded any problems, so there is nothing to send.",
                    "No logs yet");
        }
        catch (Exception ex)
        {
            // An async void handler: anything escaping here takes the app down, which would be a
            // poor way for the screen that exists to report crashes to behave.
            Log.Warning(ex, "Sharing the app logs failed.");
            await _popups.ShowErrorAsync(
                "The logs could not be prepared. Please try again.",
                "Couldn't get the logs");
        }
    }

    private async void OnSignOutClicked(object? sender, EventArgs e)
    {
        if (!_signOutGate.Confirm())
        {
            ShowExitHint();
            return;
        }

        HideExitHint();
        SignOutBtn.IsEnabled = false;
        try
        {
            await _authService.SignOutAsync();
            Preferences.Default.Remove("PrimaryCardiMemberId");
            Preferences.Default.Remove("VerifyEmailNudgeDismissed");
            // The account is the record of the health-data disclosure; this is only the hint that
            // it was confirmed, and the next caregiver on this phone must be asked afresh.
            Preferences.Default.Remove(DashboardPage.HealthDataDisclosureConfirmedKey);
            Preferences.Default.Remove(WizardLauncher.ResumeDismissedKey);
            // Consent is the person's, not the phone's: stop collecting now, and make the next
            // caregiver who signs in here say yes for themselves.
            DiagnosticsConsent.Clear();
            // Holds a name, DOB and medical notes — must not survive into the next session.
            await _drafts.ClearAsync();
            WindowNavigation.SetRootPage(this, new NavigationPage(new SignInPage()));
        }
        finally
        {
            SignOutBtn.IsEnabled = true;
        }
    }
}
