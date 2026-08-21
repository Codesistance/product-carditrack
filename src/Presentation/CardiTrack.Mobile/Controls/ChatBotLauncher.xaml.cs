using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Services;

namespace CardiTrack.Mobile.Controls;

/// <summary>
/// Code-behind for the floating member-chat launcher — see the XAML's remarks for what it is.
/// The pulse animation follows the host page's Appearing/Disappearing (an animation looping on a
/// backgrounded page is wasted battery), drag position is session-only (resets to docked
/// bottom-end on the next page load), and the chat overlay is layered into the nearest ancestor
/// Grid — which, placed as these hosts place it, is the page's root grid.
/// </summary>
public partial class ChatBotLauncher : ContentView
{
    /// <summary>The member the host page's content belongs to. Left at <see cref="Guid.Empty"/>
    /// by pages without a single-member context (Alerts list, Settings, Notifications) — the
    /// launcher then resolves the account's members itself on tap.</summary>
    public Guid MemberId { get; set; }

    public string? MemberFirstName { get; set; }

    private ContentPage? _page;
    private CancellationTokenSource? _pulseCts;
    private bool _resolving;

    public ChatBotLauncher()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object? sender, EventArgs e)
    {
        // Follow the page's visibility, not the control's Loaded alone: tab pages stay loaded
        // while another tab is on screen, and Appearing/Disappearing is the signal that tracks
        // what's actually visible (same reason DashboardPage keyed its pulse off OnAppearing).
        _page = FindPage();
        if (_page is not null)
        {
            _page.Appearing += OnPageAppearing;
            _page.Disappearing += OnPageDisappearing;
        }
        StartPulse();
    }

    private void OnUnloaded(object? sender, EventArgs e)
    {
        if (_page is not null)
        {
            _page.Appearing -= OnPageAppearing;
            _page.Disappearing -= OnPageDisappearing;
            _page = null;
        }
        _pulseCts?.Cancel();
    }

    private void OnPageAppearing(object? sender, EventArgs e) => StartPulse();
    private void OnPageDisappearing(object? sender, EventArgs e) => _pulseCts?.Cancel();

    private ContentPage? FindPage()
    {
        Element? cursor = this;
        while (cursor is not null && cursor is not ContentPage)
            cursor = cursor.Parent;
        return cursor as ContentPage;
    }

    private Grid? FindHostGrid()
    {
        Element? cursor = Parent;
        while (cursor is not null && cursor is not Grid)
            cursor = cursor.Parent;
        return cursor as Grid;
    }

    // ── Pulse ───────────────────────────────────────────────────────────────────

    private void StartPulse()
    {
        _pulseCts?.Cancel();
        _pulseCts = new CancellationTokenSource();
        _ = PulseLoopAsync(_pulseCts.Token);
    }

    private async Task PulseLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(6), ct);
                Pulse.Scale = 1;
                Pulse.Opacity = 0.55;
                var ring = Pulse.FadeToAsync(0, 1100, Easing.CubicOut);
                var expand = Pulse.ScaleToAsync(1.7, 1100, Easing.CubicOut);
                await Task.WhenAll(ring, expand, FlashButtonAsync());
            }
        }
        catch (OperationCanceledException)
        {
            // Page went off screen — the loop simply stops.
        }
    }

    private async Task FlashButtonAsync()
    {
        await Button.FadeToAsync(0.55, 250, Easing.CubicOut);
        await Button.FadeToAsync(1, 350, Easing.CubicIn);
    }

    // ── Open ────────────────────────────────────────────────────────────────────

    private async void OnTapped(object? sender, TappedEventArgs e)
    {
        if (FindHostGrid() is not { } host)
            return;

        if (MemberId != Guid.Empty)
        {
            MemberChatLauncher.ShowOverlay(host, MemberId, MemberFirstName);
            return;
        }

        // Pages without a single-member context: resolve the account's members here. One member
        // — the ordinary family case — opens directly; several ask; none quietly does nothing
        // (a fresh account with no member has nothing to chat about, and the pages that host
        // this state already surface their own onboarding paths).
        if (_resolving)
            return;
        _resolving = true;
        try
        {
            var api = ServiceHelper.GetRequiredService<ICardiTrackApiClient>();
            var members = await api.GetCardiMembersAsync();
            if (members.Count == 0)
                return;

            var chosen = members[0];
            if (members.Count > 1 && FindPage() is { } page)
            {
                // The action sheet hands back only the tapped label, so the label must identify
                // the member by itself — and the API allows two members with the same name.
                // Duplicated names get a per-member ordinal, which keeps every label unique and
                // makes the index lookup below unambiguous.
                var labels = members
                    .Select((m, i) => members.Count(x => x.Name == m.Name) > 1 ? $"{m.Name} ({i + 1})" : m.Name)
                    .ToArray();
                var picked = await page.DisplayActionSheetAsync("Ask about who?", "Cancel", null, labels);
                var index = picked is null ? -1 : Array.IndexOf(labels, picked);
                if (index < 0)
                    return;
                chosen = members[index];
            }

            MemberChatLauncher.ShowOverlay(host, chosen.Id, NameFormatting.FirstName(chosen.Name));
        }
        catch (ApiException)
        {
            // Can't reach the API to list members — the pages hosting this already show their
            // own offline/error affordances; a dead tap is better than a second error surface.
        }
        finally
        {
            _resolving = false;
        }
    }

    // ── Drag ────────────────────────────────────────────────────────────────────

    /// <summary>Where the drag started, relative to the docked position — needed because
    /// PanUpdated reports totals from gesture start, not deltas since the last event.</summary>
    private double _baseX;
    private double _baseY;

    /// <summary>Where the current drag is actually holding — the last position a Running event
    /// applied. Committed as the new base when the gesture ends, because the platform handlers
    /// raise Completed/Canceled through the (status, gestureId) constructor, whose TotalX/TotalY
    /// are always 0: trusting the terminal event's totals reset the base to the dock on every
    /// release, so the button visibly stayed put but snapped home the moment the next drag's
    /// first Running event applied "base + total".</summary>
    private double _heldX;
    private double _heldY;

    /// <summary>Drags the launcher off whatever it's obstructing. Position is session-only: it
    /// resets to docked bottom-end next time the page loads, rather than persisting a spot that
    /// might not make sense once the content underneath has changed.</summary>
    private void OnPanUpdated(object? sender, PanUpdatedEventArgs e)
    {
        switch (e.StatusType)
        {
            case GestureStatus.Started:
                // A pan that ends before any Running event fires must commit the spot the button
                // is already on, not whatever the previous gesture left in the held fields.
                _heldX = _baseX;
                _heldY = _baseY;
                break;

            case GestureStatus.Running:
                _heldX = ClampX(_baseX + e.TotalX);
                _heldY = ClampY(_baseY + e.TotalY);
                TranslationX = _heldX;
                TranslationY = _heldY;
                break;

            case GestureStatus.Completed:
            case GestureStatus.Canceled:
                // Both terminal states keep the button wherever the drag was actually holding —
                // an interrupted drag (an incoming call, a system gesture) should still leave it
                // wherever the caregiver had visibly moved it to.
                _baseX = _heldX;
                _baseY = _heldY;
                TranslationX = _baseX;
                TranslationY = _baseY;
                break;
        }
    }

    // Clamped against the host page's bounds, same constants DashboardPage used: the button can
    // roam the page but not leave it (76 keeps the full button on screen horizontally; 220
    // keeps it below the header band).
    private double ClampX(double x)
    {
        var width = _page?.Width ?? Width;
        return Math.Max(-(width - 76), Math.Min(20, x));
    }

    private double ClampY(double y)
    {
        var height = _page?.Height ?? Height;
        return Math.Max(-(height - 220), Math.Min(0, y));
    }
}
