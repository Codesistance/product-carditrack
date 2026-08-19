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

        // Photo mask — Figma "Vector 1" (101:7451), the higher of the frame's two waves.
        // The gradient shows through everywhere below this curve.
        var photoFigure = new PathFigure { StartPoint = P(0, 610), IsClosed = true };
        photoFigure.Segments.Add(new LineSegment { Point = P(0, 0) });
        photoFigure.Segments.Add(new LineSegment { Point = P(440, 0) });
        photoFigure.Segments.Add(new LineSegment { Point = P(440, 508.577) });
        photoFigure.Segments.Add(new BezierSegment
        {
            Point1 = P(409.424, 554.545),
            Point2 = P(316.203, 569.868),
            Point3 = P(224.475, 560.383)
        });
        photoFigure.Segments.Add(new BezierSegment
        {
            Point1 = P(151.092, 552.794),
            Point2 = P(44.2486, 590.299),
            Point3 = P(0, 610)
        });
        var photoGeometry = new PathGeometry();
        photoGeometry.Figures.Add(photoFigure);
        HeroClipBorder.Clip = photoGeometry;

        // Gradient shape — Figma "Vector 2" (101:7449), whose own bottom edge is the second,
        // lower wave. The page's white background shows below it, so the top of the solid
        // area is wavy; between the two curves the gradient reads as a tapering ribbon that
        // vanishes into the bottom-left corner.
        var gradientFigure = new PathFigure { StartPoint = P(236.116, 580.084), IsClosed = true };
        gradientFigure.Segments.Add(new BezierSegment
        {
            Point1 = P(149.765, 556.151),
            Point2 = P(42.7257, 590.056),
            Point3 = P(0, 610)
        });
        gradientFigure.Segments.Add(new LineSegment { Point = P(0, 0) });
        gradientFigure.Segments.Add(new LineSegment { Point = P(440, 0) });
        gradientFigure.Segments.Add(new LineSegment { Point = P(440, 580.084) });
        gradientFigure.Segments.Add(new BezierSegment
        {
            Point1 = P(400.273, 596.866),
            Point2 = P(344.055, 610),
            Point3 = P(236.116, 580.084)
        });
        var gradientGeometry = new PathGeometry();
        gradientGeometry.Figures.Add(gradientFigure);
        GradientClipBorder.Clip = gradientGeometry;
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
            SlideTitle.FadeToAsync(0, 110, Easing.CubicIn),
            SlideTitle.TranslateToAsync(0, -10, 110, Easing.CubicIn),
            SlideSubtitle.FadeToAsync(0, 110, Easing.CubicIn),
            SlideSubtitle.TranslateToAsync(0, -10, 110, Easing.CubicIn));

        // A newer slide change took over while we faded out — let it own the fade-in.
        if (seq != _textSwapSeq)
            return;

        ApplyText(slide);
        SlideTitle.TranslationY = 14;
        SlideSubtitle.TranslationY = 14;

        await Task.WhenAll(
            SlideTitle.FadeToAsync(1, 220, Easing.CubicOut),
            SlideTitle.TranslateToAsync(0, 0, 220, Easing.CubicOut),
            SlideSubtitle.FadeToAsync(1, 220, Easing.CubicOut),
            SlideSubtitle.TranslateToAsync(0, 0, 220, Easing.CubicOut));
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
