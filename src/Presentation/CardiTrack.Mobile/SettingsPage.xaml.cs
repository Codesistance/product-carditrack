using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Core.Auth;
using CardiTrack.Mobile.Core.Onboarding;
using CardiTrack.Mobile.Onboarding;
using CardiTrack.Mobile.Services;
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
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        AccountNameLabel.Text = _authService.CurrentUserName ?? "Your account";
        AccountEmailLabel.Text = _authService.CurrentUserEmail ?? string.Empty;
        VersionLabel.Text = $"{AppInfo.Current.VersionString} ({AppInfo.Current.BuildString})";
        _ = LoadMutesAsync();
        _ = LoadNotificationSummaryAsync();
    }

    /// <summary>The row's one line says what is set, so the page answers before it is tapped.</summary>
    private async Task LoadNotificationSummaryAsync()
    {
        try
        {
            var prefs = await _api.GetNotificationPreferencesAsync();
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
        catch (ApiException)
        {
            NotificationSummary.Text = "Quiet hours, lock-screen detail, what to hear about";
        }
    }

    private async void OnNotificationPreferencesTapped(object? sender, TappedEventArgs e) =>
        await Shell.Current.GoToAsync(NotificationPreferencesPage.Route);

    // The same reset-link call the signed-out Forgot Password screen makes, sent to the
    // signed-in address without asking for it again — there is nothing else to type.
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
        await Navigation.PushModalAsync(new LegalDocumentPage("Terms of Service", LegalDocumentPage.TermsUrl));

    private async void OnPrivacyTapped(object? sender, TappedEventArgs e) =>
        await Navigation.PushModalAsync(new LegalDocumentPage("Privacy Policy", LegalDocumentPage.PrivacyUrl));

    // Starts the erasure request the privacy policy promises (30 days), the same way the
    // policy page itself does — a pre-addressed email — until an endpoint exists.
    // The tick box is the confirmation: the card above it has already said what happens, so
    // the button does not ask again. It stays off — and reads off — until the box is ticked.
    private void OnDeleteConfirmChanged(object? sender, CheckedChangedEventArgs e)
    {
        DeleteAccountBtn.IsEnabled = e.Value;
        DeleteAccountBtn.Opacity = e.Value ? 1 : 0.5;
    }

    private void OnDeleteConfirmLabelTapped(object? sender, TappedEventArgs e) =>
        DeleteConfirmCheck.IsChecked = !DeleteConfirmCheck.IsChecked;

    private async void OnDeleteAccountClicked(object? sender, EventArgs e)
    {
        if (!DeleteConfirmCheck.IsChecked)
            return;

        // The request has to name the account; without the address support cannot act on it.
        var email = _authService.CurrentUserEmail;
        if (string.IsNullOrWhiteSpace(email))
        {
            await _popups.ShowWarningAsync(
                "We don't have an email address for this account. Please email support@carditrack.com from the address you sign in with.",
                "Can't start the request");
            return;
        }

        var subject = Uri.EscapeDataString("Delete my account");
        var body = Uri.EscapeDataString($"Please delete the CardiTrack account for {email}.");
        try
        {
            await Launcher.Default.OpenAsync(new Uri($"mailto:support@carditrack.com?subject={subject}&body={body}"));
        }
        catch (Exception)
        {
            await _popups.ShowInfoAsync("Email support@carditrack.com with the subject \"Delete my account\".", "No mail app found");
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
        try
        {
            var mutes = await _api.GetNotificationMutesAsync();
            RenderMutes(mutes);
        }
        catch (ApiException)
        {
            // Settings must still open if this call fails; the section simply does not appear.
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
            Preferences.Default.Remove(WizardLauncher.ResumeDismissedKey);
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
