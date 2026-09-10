using Microsoft.Maui.Accessibility;
using Microsoft.Maui.Controls.Shapes;

namespace CardiTrack.Mobile.Controls;

/// <summary>
/// The one-second "Updating…" that marks a saved snapshot being replaced by live data. It goes
/// up just before the fresh render and comes down on its own, so the caregiver sees the
/// replacement happen rather than the numbers silently changing under their eyes.
/// </summary>
/// <remarks>
/// <para>
/// An in-page overlay, not a pushed modal: the last child of the page's root grid, spanning
/// every row. Pushing a page would put it on the modal stack, and <c>ScreenRefresh.IsOnScreen</c>
/// treats anything there as "the caregiver is not looking at this screen" — which would pause
/// the very refresh that just finished. The exit hint on the dashboard is the same shape.
/// </para>
/// <para>
/// The timed show swallows taps for its second. The content under it is mid-replacement — a
/// list that is about to reflow — and a tap that landed on the old row and acted on the new
/// one is worse than a tap that did nothing. The timed show is for a saved-to-fresh
/// transition, never a cold load (the skeleton speaks there) and never a background tick.
/// <see cref="ShowUntilHiddenAsync"/> is the same scrim held until the page hides it, for a
/// wait the page owns (preparing an export).
/// </para>
/// </remarks>
public sealed class UpdatingOverlay : Grid
{
    /// <summary>How long the overlay stays up, first frame to last.</summary>
    public static readonly TimeSpan DefaultHold = TimeSpan.FromSeconds(1);

    // The popup scrims' timings, so this reads as the same kind of object.
    private const uint FadeInMs = 140;
    private const uint FadeOutMs = 100;

    private readonly ActivityIndicator _spinner;
    private readonly Label _message;
    private CancellationTokenSource? _hold;

    public UpdatingOverlay()
    {
        IsVisible = false;
        Opacity = 0;
        BackgroundColor = ControlResources.Color("ScrimOverlay", Color.FromArgb("#80343434"));
        // Swallows taps so nothing under the scrim is reached while it is up.
        GestureRecognizers.Add(new TapGestureRecognizer());
        AutomationProperties.SetIsInAccessibleTree(this, false);

        _spinner = new ActivityIndicator
        {
            IsRunning = false,
            WidthRequest = 22,
            HeightRequest = 22,
            VerticalOptions = LayoutOptions.Center,
        };
        // Decorative — the words are the message.
        AutomationProperties.SetIsInAccessibleTree(_spinner, false);

        _message = new Label
        {
            Text = "Updating…",
            VerticalOptions = LayoutOptions.Center,
        };
        ControlResources.ApplyStyle(_message, "Body1SemiBoldDark");

        var card = new Border
        {
            BackgroundColor = ControlResources.Color("White", Colors.White),
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 16 },
            Padding = new Thickness(20, 14),
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            Content = new HorizontalStackLayout
            {
                Spacing = 12,
                Children = { _spinner, _message },
            },
        };
        if (ControlResources.Brush("CardShadowBrush") is { } shadow)
            card.Shadow = new Shadow { Brush = shadow, Opacity = 0.25f, Radius = 14, Offset = new Point(0, 4) };
        SemanticProperties.SetDescription(card, "Updating with the latest data");
        AutomationProperties.SetIsInAccessibleTree(card, true);

        Children.Add(card);

        // Pages are transient; an overlay left up on a page that has gone must not hold its
        // timer, and a page shown again must not open onto a stale scrim.
        Unloaded += (_, _) => Hide();
    }

    /// <summary>
    /// Shows the overlay for <paramref name="hold"/> (default <see cref="DefaultHold"/>), then
    /// fades it out. Calling it again while it is up restarts the hold without blinking. Call
    /// from the UI thread; fire-and-forget is the intended use, so the fresh render can happen
    /// under the scrim in the same turn.
    /// </summary>
    public async Task ShowAsync(TimeSpan? hold = null)
    {
        _hold?.Cancel();
        var cts = new CancellationTokenSource();
        _hold = cts;
        _message.Text = "Updating…";

        Appear("Updating…");

        try
        {
            var wait = (hold ?? DefaultHold) - TimeSpan.FromMilliseconds(FadeOutMs);
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, cts.Token);
            await this.FadeToAsync(0, FadeOutMs);
        }
        catch (OperationCanceledException)
        {
            // A newer show, or Hide, owns the state now — the check below sees that and
            // leaves the overlay alone.
        }
        finally
        {
            if (ReferenceEquals(_hold, cts))
            {
                _hold = null;
                Conceal();
            }

            // Ours to dispose on every path: whoever cancelled it has already dropped it.
            cts.Dispose();
        }
    }

    /// <summary>
    /// Stays up until <see cref="Hide"/> — for a wait the page owns, like preparing an export.
    /// Cancels a timed <see cref="ShowAsync"/> without blinking the scrim off.
    /// </summary>
    public Task ShowUntilHiddenAsync(string message)
    {
        _hold?.Cancel();
        _hold = null;
        _message.Text = message;
        Appear(message);
        return Task.CompletedTask;
    }

    private void Appear(string announcement)
    {
        if (!IsVisible)
        {
            Opacity = 0;
            IsVisible = true;
            _spinner.IsRunning = true;
            Announce(announcement);
        }
        if (Opacity < 1)
            _ = this.FadeToAsync(1, FadeInMs);
    }

    public void Hide()
    {
        _hold?.Cancel();
        _hold = null;
        Conceal();
    }

    private void Conceal()
    {
        IsVisible = false;
        Opacity = 0;
        _spinner.IsRunning = false;
    }

    private static void Announce(string message)
    {
        try
        {
            SemanticScreenReader.Default.Announce(message);
        }
        catch (Exception)
        {
            // Not every platform has a screen reader service; the label is still readable.
        }
    }
}
