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
            if (members.Count > 1)
            {
                // The chooser hands back only the tapped label, so the label must identify the
                // member by itself — and the API allows two members with the same name.
                // Duplicated names get a per-member ordinal, which keeps every label unique and
                // makes the index lookup below unambiguous.
                var labels = members
                    .Select((m, i) => members.Count(x => x.Name == m.Name) > 1 ? $"{m.Name} ({i + 1})" : m.Name)
                    .ToArray();
                // The app's own chooser rather than the platform action sheet, which was the one
                // system-drawn surface left on the pages that host this launcher.
                var picked = await ServiceHelper.GetRequiredService<IPopupService>()
                    .ChooseAsync("Ask about who?", "Cancel", labels);
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

    private const string FollowAnimation = "chat-follow";
    private const string GlideAnimation = "chat-glide";

    /// <summary>Where the drag started, relative to the docked position — needed because
    /// PanUpdated reports totals from gesture start, not deltas since the last event.</summary>
    private double _baseX;
    private double _baseY;

    /// <summary>Where the finger currently says the button should be — the last position a
    /// Running event resolved to, clamped. The button eases toward it rather than jumping to it
    /// (see <see cref="FollowTo"/>). Read back when the gesture ends, because the platform
    /// handlers raise Completed/Canceled through the (status, gestureId) constructor, whose
    /// TotalX/TotalY are always 0: trusting the terminal event's totals reset the base to the
    /// dock on every release, so the button visibly stayed put but snapped home the moment the
    /// next drag's first Running event applied "base + total".</summary>
    private double _heldX;
    private double _heldY;

    /// <summary>The finger's speed over the last few events, in dp per millisecond, smoothed so
    /// one jittery sample cannot fling the button. What the release glide is thrown with.</summary>
    private double _velocityX;
    private double _velocityY;
    private long _lastSampleAt;

    /// <summary>
    /// Drags the launcher off whatever it's obstructing. Position is session-only: it resets to
    /// docked bottom-end next time the page loads, rather than persisting a spot that might not
    /// make sense once the content underneath has changed.
    /// </summary>
    /// <remarks>
    /// It used to set TranslationX/Y straight from each Running event, and felt stiff: the events
    /// arrive every 16–50 ms (measured on the emulator), so the button stepped after the finger
    /// at 20–30 fps and then stopped dead on release. Now each event only moves the target, the
    /// button eases toward it over the next frames, and letting go throws it a short way along
    /// the finger's last velocity before it settles — a drag that flows rather than one that
    /// ratchets.
    /// </remarks>
    private void OnPanUpdated(object? sender, PanUpdatedEventArgs e)
    {
        switch (e.StatusType)
        {
            case GestureStatus.Started:
                // Grabbing the button mid-glide takes it from wherever it actually is, not from
                // where the glide was going to leave it — otherwise the first Running event would
                // snap it to the glide's destination and it would jump under the finger.
                this.AbortAnimation(FollowAnimation);
                this.AbortAnimation(GlideAnimation);
                _baseX = TranslationX;
                _baseY = TranslationY;
                // A pan that ends before any Running event fires must commit the spot the button
                // is already on, not whatever the previous gesture left in the held fields.
                _heldX = _baseX;
                _heldY = _baseY;
                _velocityX = 0;
                _velocityY = 0;
                _lastSampleAt = Environment.TickCount64;
                break;

            case GestureStatus.Running:
                var x = ClampX(_baseX + e.TotalX);
                var y = ClampY(_baseY + e.TotalY);
                var now = Environment.TickCount64;
                var elapsed = now - _lastSampleAt;
                if (elapsed > 0)
                {
                    // Exponential smoothing rather than the raw last delta: two events 16 ms
                    // apart with a 1 dp jitter between them would otherwise read as a fling.
                    const double weight = 0.5;
                    _velocityX = weight * (x - _heldX) / elapsed + (1 - weight) * _velocityX;
                    _velocityY = weight * (y - _heldY) / elapsed + (1 - weight) * _velocityY;
                }
                _lastSampleAt = now;
                _heldX = x;
                _heldY = y;
                FollowTo(x, y);
                break;

            case GestureStatus.Completed:
            case GestureStatus.Canceled:
                // Both terminal states keep the button wherever the drag was actually holding —
                // an interrupted drag (an incoming call, a system gesture) should still leave it
                // wherever the caregiver had visibly moved it to — and then let it run on a
                // little. A finger that paused before lifting has no throw left in it.
                if (Environment.TickCount64 - _lastSampleAt > 80)
                {
                    _velocityX = 0;
                    _velocityY = 0;
                }
                GlideFrom(_heldX, _heldY);
                break;
        }
    }

    /// <summary>
    /// Eases the button from wherever it is to the finger's latest position over the next few
    /// frames. Short enough (90 ms) that it never visibly trails the finger, long enough to span
    /// the gap between two events so the motion is continuous instead of stepped.
    /// </summary>
    private void FollowTo(double x, double y)
    {
        this.AbortAnimation(GlideAnimation);
        this.AbortAnimation(FollowAnimation);
        var fromX = TranslationX;
        var fromY = TranslationY;
        new Animation(v =>
        {
            TranslationX = fromX + (x - fromX) * v;
            TranslationY = fromY + (y - fromY) * v;
        }, 0, 1, Easing.SinOut).Commit(this, FollowAnimation, 16, 90);
    }

    /// <summary>
    /// The release: carries the button on along the last velocity for a beat and decelerates
    /// into the clamped bounds, so a flick lands it somewhere further and a gentle let-go just
    /// settles. The destination becomes the base the next drag starts from.
    /// </summary>
    private void GlideFrom(double x, double y)
    {
        this.AbortAnimation(FollowAnimation);
        this.AbortAnimation(GlideAnimation);

        // 140 ms of projection is a nudge, not a launch: a brisk flick moves it a further
        // button-width or so, never across the page.
        const double throwMs = 140;
        var toX = ClampX(x + _velocityX * throwMs);
        var toY = ClampY(y + _velocityY * throwMs);
        _baseX = toX;
        _baseY = toY;
        _heldX = toX;
        _heldY = toY;

        var fromX = TranslationX;
        var fromY = TranslationY;
        new Animation(v =>
        {
            TranslationX = fromX + (toX - fromX) * v;
            TranslationY = fromY + (toY - fromY) * v;
        }, 0, 1, Easing.CubicOut).Commit(this, GlideAnimation, 16, 320);
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
