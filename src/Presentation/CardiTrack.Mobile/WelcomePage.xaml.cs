using CardiTrack.Mobile.Services;
using Microsoft.Maui.Controls.Shapes;
using PointF = Microsoft.Maui.Graphics.Point;

namespace CardiTrack.Mobile;

public partial class WelcomePage : ContentPage
{
    // The wave curve, authored against a 440×610 reference canvas (Figma node 101:7487's own
    // path) and rescaled to the hero's actual rendered size in OnHeroSizeChanged, rather than
    // baked into a fixed-resolution image — see the XAML comment above the hero Grid.
    private const double ReferenceWidth = 440;
    private const double ReferenceHeight = 610;

    /// <summary>How long each slide holds before the carousel advances on its own.</summary>
    private static readonly TimeSpan AutoAdvanceInterval = TimeSpan.FromSeconds(5);

    public IReadOnlyList<WelcomeSlide> Slides { get; } = WelcomeSlide.DefaultSlides;

    private readonly BoxView[] _indicators;
    private readonly IDispatcherTimer _autoAdvance;

    // The title/subtitle live outside the CarouselView (the wave backdrop is a fixed overlay,
    // so the text can't ride inside the item template without dragging the wave along). They
    // crossfade in sync with slide changes instead; the sequence number lets a newer change
    // abandon an older one mid-flight.
    private int _textSwapSeq;
    private bool _textShown;

    public WelcomePage()
    {
        InitializeComponent();
        _indicators = [Ind0, Ind1, Ind2];

        _autoAdvance = Dispatcher.CreateTimer();
        _autoAdvance.Interval = AutoAdvanceInterval;
        _autoAdvance.Tick += (_, _) =>
            SlideCarousel.Position = (SlideCarousel.Position + 1) % Slides.Count;

        // Manual swipes land here too, so every change — user or timer — restarts the clock
        // and the user never has the carousel yanked away right after they touched it.
        SlideCarousel.CurrentItemChanged += (_, _) =>
        {
            RestartAutoAdvance();
            UpdateSlideState();
        };
    }

    private void OnHeroSizeChanged(object? sender, EventArgs e)
    {
        var width = HeroClipBorder.Width;
        var height = HeroClipBorder.Height;
        if (width <= 0 || height <= 0)
            return;

        var sx = width / ReferenceWidth;
        var sy = height / ReferenceHeight;
        PointF P(double x, double y) => new(x * sx, y * sy);

        // Photo-visible region: almost the full frame, its bottom edge following the wave curve
        // instead of a straight line. Everything outside this clip shows the gradient BoxView
        // painted behind it — always full-bleed, so there is no path for a gap to reappear in.
        var clipFigure = new PathFigure { StartPoint = P(236.116, 580.084), IsClosed = true };
        clipFigure.Segments.Add(new BezierSegment
        {
            Point1 = P(149.765, 556.151),
            Point2 = P(42.7257, 590.056),
            Point3 = P(0, 610)
        });
        clipFigure.Segments.Add(new LineSegment { Point = P(0, 0) });
        clipFigure.Segments.Add(new LineSegment { Point = P(440, 0) });
        clipFigure.Segments.Add(new LineSegment { Point = P(440, 580.084) });
        clipFigure.Segments.Add(new BezierSegment
        {
            Point1 = P(400.273, 596.866),
            Point2 = P(344.055, 610),
            Point3 = P(236.116, 580.084)
        });
        var clipGeometry = new PathGeometry();
        clipGeometry.Figures.Add(clipFigure);
        HeroClipBorder.Clip = clipGeometry;

        // The stroke overlay traces only the curved segment (open figure — the straight frame
        // edges above aren't part of the visible seam) at the same scale as the clip.
        var strokeFigure = new PathFigure { StartPoint = P(440, 580.084), IsClosed = false };
        strokeFigure.Segments.Add(new BezierSegment
        {
            Point1 = P(400.273, 596.866),
            Point2 = P(344.055, 610),
            Point3 = P(236.116, 580.084)
        });
        strokeFigure.Segments.Add(new BezierSegment
        {
            Point1 = P(149.765, 556.151),
            Point2 = P(42.7257, 590.056),
            Point3 = P(0, 610)
        });
        var strokeGeometry = new PathGeometry();
        strokeGeometry.Figures.Add(strokeFigure);
        HeroWaveStroke.Data = strokeGeometry;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        UpdateSlideState();
        _autoAdvance.Start();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _autoAdvance.Stop();
    }

    private void RestartAutoAdvance()
    {
        // Fires during construction before the page appears; only reset a running clock.
        if (!_autoAdvance.IsRunning)
            return;
        _autoAdvance.Stop();
        _autoAdvance.Start();
    }

    private void UpdateSlideState()
    {
        if (SlideCarousel.CurrentItem is not WelcomeSlide current)
            return;

        var idx = IndexOf(current);

        for (var i = 0; i < _indicators.Length; i++)
        {
            _indicators[i].WidthRequest = i == idx ? 32 : 8;
            _indicators[i].Color = i == idx
                ? (Color)App.Current!.Resources["ActiveIndicator"]
                : (Color)App.Current!.Resources["InactiveIndicator"];
        }

        // First display renders instantly; later changes crossfade alongside the photo.
        _ = SwapTextAsync(current, animate: _textShown);
        _textShown = true;
    }

    private async Task SwapTextAsync(WelcomeSlide slide, bool animate)
    {
        var seq = ++_textSwapSeq;

        if (!animate)
        {
            ApplyText(slide);
            return;
        }

        SlideTitle.CancelAnimations();
        SlideSubtitle.CancelAnimations();

        await Task.WhenAll(
            SlideTitle.FadeTo(0, 110, Easing.CubicIn),
            SlideTitle.TranslateTo(0, -10, 110, Easing.CubicIn),
            SlideSubtitle.FadeTo(0, 110, Easing.CubicIn),
            SlideSubtitle.TranslateTo(0, -10, 110, Easing.CubicIn));

        // A newer slide change took over while we faded out — let it own the fade-in.
        if (seq != _textSwapSeq)
            return;

        ApplyText(slide);
        SlideTitle.TranslationY = 14;
        SlideSubtitle.TranslationY = 14;

        await Task.WhenAll(
            SlideTitle.FadeTo(1, 220, Easing.CubicOut),
            SlideTitle.TranslateTo(0, 0, 220, Easing.CubicOut),
            SlideSubtitle.FadeTo(1, 220, Easing.CubicOut),
            SlideSubtitle.TranslateTo(0, 0, 220, Easing.CubicOut));
    }

    private void ApplyText(WelcomeSlide slide)
    {
        SlideTitle.Text = slide.Title;
        SlideSubtitle.Text = slide.Subtitle;
        SlideSubtitle.Margin = new Thickness(24, slide.SubtitleTopMargin, 24, 0);
        SlideSubtitle.MaximumWidthRequest = slide.SubtitleMaxWidth;
    }

    private int IndexOf(WelcomeSlide slide)
    {
        for (var i = 0; i < Slides.Count; i++)
        {
            if (ReferenceEquals(Slides[i], slide))
                return i;
        }
        return -1;
    }

    private void OnCtaTapped(object? sender, EventArgs e)
    {
        WindowNavigation.SetRootPage(this, new NavigationPage(new CreateAccountPage()));
    }

    private void OnSignInTapped(object? sender, EventArgs e)
    {
        WindowNavigation.SetRootPage(this, new NavigationPage(new SignInPage()));
    }

    private async void OnTermsTapped(object? sender, EventArgs e)
    {
        await ServiceHelper.GetRequiredService<IPopupService>()
            .ShowInfoAsync("Terms and privacy will open here.", "Terms & Privacy");
    }
}
