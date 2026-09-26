using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Core.Auth;
using CardiTrack.Mobile.Services;

namespace CardiTrack.Mobile;

/// <summary>
/// Shows <see cref="MemberChatPage"/> as an overlay layered into the host page's own root grid —
/// not a modal push. MAUI's modal push composites the new page as a fully opaque screen on
/// Android, so a "transparent" pushed page never actually shows the previous page dimmed behind
/// it; layering into the host's visual tree is the only way the background genuinely stays
/// visible. The overlay spans every row of the host grid, so it dims the header and nav bar too.
/// </summary>
internal static class MemberChatLauncher
{
    public static void ShowOverlay(Grid hostRoot, Guid memberId, string? memberFirstName)
    {
        // One at a time: a second tap while the overlay is up must not stack another.
        if (hostRoot.Children.OfType<MemberChatPage>().Any())
            return;

        // The caregiver's own first name, for the greeting's hello — the same split the
        // dashboard header makes of the signed-in name.
        var caregiverFirstName = ServiceHelper.GetRequiredService<IAuthService>().CurrentUserName?.Split(' ')[0];
        var overlay = new MemberChatPage(
            ServiceHelper.GetRequiredService<ICardiTrackApiClient>(), memberId, memberFirstName, caregiverFirstName);

        Grid.SetRow(overlay, 0);
        Grid.SetRowSpan(overlay, Math.Max(1, hostRoot.RowDefinitions.Count));

#if ANDROID
        var back = new BackClosesOverlay(overlay, () => hostRoot.Children.Remove(overlay));
        overlay.Loaded += (_, _) => back.Attach();
        overlay.Unloaded += (_, _) => back.Detach();
        overlay.CloseRequested += (_, _) => back.Detach();
#endif
        overlay.CloseRequested += (_, _) => hostRoot.Children.Remove(overlay);
        hostRoot.Children.Add(overlay);
    }

#if ANDROID
    /// <summary>
    /// Android's back closes the sheet, as its down button and the scrim do. The sheet is not a
    /// page, so no page's back handling reaches it: without this a back press went to whatever
    /// the host page does with one — on the dashboard, the ask-twice-before-leaving hold — and the
    /// caregiver left the screen with the sheet still up over it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A callback on the activity's <see cref="AndroidX.Activity.OnBackPressedDispatcher"/>, the
    /// same route <c>MainActivity</c>'s own back handling takes and for the same reason (predictive
    /// back never raises a page's <c>OnBackButtonPressed</c> here). The dispatcher serves the
    /// most recently added callback first, so one added while the sheet is on screen runs ahead
    /// of MainActivity's and MAUI's.
    /// </para>
    /// <para>
    /// Held only while the sheet is in the window: added on <c>Loaded</c>, removed on
    /// <c>Unloaded</c> and on close, so a sheet on a page the caregiver has navigated away from
    /// never swallows a back meant for the page they are on. A press that is not the sheet's —
    /// a popup is over it, or another page is in front — is handed back to the dispatcher, the
    /// way MainActivity hands on a press it has no answer for.
    /// </para>
    /// </remarks>
    private sealed class BackClosesOverlay(MemberChatPage overlay, Action close)
        : AndroidX.Activity.OnBackPressedCallback(enabled: true)
    {
        private AndroidX.Activity.ComponentActivity? _activity;

        public void Attach()
        {
            if (_activity is not null
                || Microsoft.Maui.ApplicationModel.Platform.CurrentActivity
                    is not AndroidX.Activity.ComponentActivity activity)
            {
                return;
            }

            _activity = activity;
            activity.OnBackPressedDispatcher.AddCallback(this);
        }

        public void Detach()
        {
            if (_activity is null)
                return;

            _activity = null;
            Remove();
        }

        public override void HandleOnBackPressed()
        {
            if (IsInFront())
            {
                close();
                Detach();
                return;
            }

            // Not ours. Disabling first is what stops the re-dispatch reaching this again.
            var activity = _activity;
            Enabled = false;
            try
            {
                activity?.OnBackPressedDispatcher.OnBackPressed();
            }
            finally
            {
                Enabled = true;
            }
        }

        /// <summary>
        /// The sheet is what the caregiver is looking at: still in its host, no popup over it, and
        /// its host the page in front.
        /// </summary>
        private bool IsInFront()
        {
            if (overlay.Parent is null)
                return false;

            var window = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault();
            if (window?.Page?.Navigation.ModalStack.Count > 0)
                return false;

            Element? host = overlay;
            while (host is not null and not Page)
                host = host.Parent;

            return Shell.Current?.CurrentPage is not { } current || ReferenceEquals(current, host);
        }
    }
#endif
}
