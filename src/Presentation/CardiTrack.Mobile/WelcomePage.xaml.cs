using App = Microsoft.Maui.Controls.Application;

namespace CardiTrack.Mobile;

public partial class WelcomePage : ContentPage
{
    public IReadOnlyList<WelcomeSlide> Slides { get; } = WelcomeSlide.DefaultSlides;

    private readonly BoxView[] _indicators;

    public WelcomePage()
    {
        InitializeComponent();
        _indicators = [Ind0, Ind1, Ind2];
        SlideCarousel.CurrentItemChanged += (_, _) => UpdateSlideState();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        UpdateSlideState();
    }

    private void UpdateSlideState()
    {
        if (SlideCarousel.CurrentItem is not WelcomeSlide current)
            return;

        var idx = IndexOf(current);

        SlideTitle.Text = current.Title;
        SlideSubtitle.Text = current.Subtitle;
        SlideSubtitle.Margin = new Thickness(40, current.SubtitleTopMargin, 40, 0);

        for (var i = 0; i < _indicators.Length; i++)
        {
            _indicators[i].WidthRequest = i == idx ? 32 : 8;
            _indicators[i].Color = i == idx
                ? (Color)App.Current!.Resources["ActiveIndicator"]
                : (Color)App.Current!.Resources["InactiveIndicator"];
        }
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

    private void OnSignUpTapped(object? sender, EventArgs e)
    {
        WindowNavigation.SetRootPage(this, new NavigationPage(new CreateAccountPage()));
    }

    private async void OnTermsTapped(object? sender, EventArgs e)
    {
        await DisplayAlertAsync("Terms & Privacy", "Terms and privacy will open here.", "OK");
    }
}
