using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Services;

namespace CardiTrack.Mobile.Controls;

/// <summary>
/// Code-behind for the floating member-chat launcher — see the XAML's remarks for what it is.
/// The pulse animation follows the host page's Appearing/Disappearing (an animation looping on a
/// backgrounded page is wasted battery), drag position is shared by every launcher in the app
/// for the session (see <see cref="s_sharedX"/>), and the chat overlay is layered into the
/// nearest ancestor Grid — which, placed as these hosts place it, is the page's root grid.
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

    /// <summary>
    /// Where the launcher is, app-wide. Every page hosts its own instance, and each used to
    /// start docked bottom-end — so a caregiver who had dragged it out of the way on the
    /// Dashboard found it back in the way on Details, and back again on the next page. One
    /// position for the session: wherever the last drag left it is where the next page shows it.
    /// Relative to the docked corner, the same on every host, so the same numbers land it in
    /// the same place. Not persisted across launches — the content it was moved off will not
    /// be the same content tomorrow.
    /// </summary>
    private static double s_sharedX;
    private static double s_sharedY;

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
        TakeSharedPosition();
        StartPulse();
    }

    /// <summary>
    /// Puts this instance where the last drag on any page left the launcher. Clamped against
    /// this page, since a spot that fitted a taller host may not fit this one — and clamped
    /// again once the page has a size, because at Loaded it can still be measuring at zero and
    /// the clamp would pin everything to the dock.
    /// </summary>
    private void TakeSharedPosition()
    {
        Place();
        if (_page is { } page && page.Width <= 0)
        {
            void OnSized(object? s, EventArgs e)
            {
                page.SizeChanged -= OnSized;
                Place();
            }
            page.SizeChanged += OnSized;
        }

        void Place()
        {
            _baseX = ClampX(s_sharedX);
            _baseY = ClampY(s_sharedY);
            _heldX = _baseX;
            _heldY = _baseY;
            TranslationX = _baseX;
            TranslationY = _baseY;
        }
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
    private const string LiftAnimation = "chat-lift";

    /// <summary>How far behind the finger the button trails, in ms. Long enough to feel like
    /// weight, short enough that it never reads as lag under a slow drag.</summary>
    private const uint FollowMs = 160;

    /// <summary>How much of the last velocity the release carries — a flick moves it a few
    /// button-widths, a gentle let-go barely more than where it was.</summary>
    private const double ThrowMs = 260;
    private const uint GlideMs = 560;

    /// <summary>The button grows a little under the finger, the way a picked-up thing comes
    /// toward you, and settles back as the glide ends.</summary>
    private const double LiftScale = 1.1;

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
    /// Drags the launcher off whatever it's obstructing. Where it lands is shared with every
    /// other page's launcher for the rest of the session (<see cref="s_sharedX"/>).
    /// </summary>
    /// <remarks>
    /// It used to set TranslationX/Y straight from each Running event, and felt stiff: the events
    /// arrive every 16–50 ms (measured on the emulator), so the button stepped after the finger
    /// at 20–30 fps and then stopped dead on release. Now the button lifts under the finger,
    /// each event only moves the target it trails toward with a little weight, and letting go
    /// throws it along the finger's last velocity into a long settle — a drag that flows rather
    /// than one that ratchets.
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
                Lift(LiftScale, 140);
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
    /// Eases the button from wherever it is toward the finger's latest position over
    /// <see cref="FollowMs"/>. Every event restarts it from the button's current spot, so a
    /// finger that keeps moving keeps the button in one continuous, slightly trailing motion
    /// instead of a series of steps.
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
        }, 0, 1, Easing.SinOut).Commit(this, FollowAnimation, 16, FollowMs);
    }

    private void Lift(double scale, uint ms)
    {
        this.AbortAnimation(LiftAnimation);
        var from = Scale;
        new Animation(v => Scale = from + (scale - from) * v, 0, 1, Easing.CubicOut)
            .Commit(this, LiftAnimation, 16, ms);
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

        var toX = ClampX(x + _velocityX * ThrowMs);
        var toY = ClampY(y + _velocityY * ThrowMs);
        _baseX = toX;
        _baseY = toY;
        _heldX = toX;
        _heldY = toY;
        s_sharedX = toX;
        s_sharedY = toY;

        var fromX = TranslationX;
        var fromY = TranslationY;
        // Quartic rather than cubic: fast off the finger, then a long, soft deceleration —
        // most of the distance is covered in the first third and the rest is the settle. The
        // lift comes down over the same glide, so the button lands as it stops.
        new Animation(v =>
        {
            TranslationX = fromX + (toX - fromX) * v;
            TranslationY = fromY + (toY - fromY) * v;
        }, 0, 1, new Easing(v => 1 - Math.Pow(1 - v, 4))).Commit(this, GlideAnimation, 16, GlideMs);
        Lift(1, GlideMs);
    }

    // Clamped against the host page's bounds, same constants DashboardPage used: the button can
    // roam the page but not leave it (76 keeps the full button on screen horizontally; 220
    // keeps it below the header band).
    private double ClampX(double x)
    {
        var width = _page?.Width ?? Width;
        return width <= 0 ? x : Math.Max(-(width - 76), Math.Min(20, x));
    }

    private double ClampY(double y)
    {
        var height = _page?.Height ?? Height;
        return height <= 0 ? y : Math.Max(-(height - 220), Math.Min(0, y));
    }
}
