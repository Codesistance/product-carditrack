namespace CardiTrack.Mobile.Controls;

public partial class WizardHeader : ContentView
{
    public static readonly BindableProperty TitleProperty =
        BindableProperty.Create(nameof(Title), typeof(string), typeof(WizardHeader), string.Empty);

    public static readonly BindableProperty StepProperty =
        BindableProperty.Create(nameof(Step), typeof(string), typeof(WizardHeader), string.Empty);

    public static readonly BindableProperty ProgressProperty =
        BindableProperty.Create(nameof(Progress), typeof(double), typeof(WizardHeader), 0d,
            propertyChanged: (b, _, v) =>
            {
                var header = (WizardHeader)b;
                header.StepProgress.IsVisible = (double)v > 0;
                header.UpdateProgressFill();

                var percent = (int)Math.Round(Math.Clamp((double)v, 0d, 1d) * 100);
                SemanticProperties.SetDescription(header.StepProgress, $"Wizard progress: {percent} percent");
            });

    public static readonly BindableProperty IsBackVisibleProperty =
        BindableProperty.Create(nameof(IsBackVisible), typeof(bool), typeof(WizardHeader), true,
            propertyChanged: (b, _, v) => ((WizardHeader)b).BackButton.IsVisible = (bool)v);

    public event EventHandler? BackRequested;

    public WizardHeader()
    {
        InitializeComponent();
        StepProgress.SizeChanged += (_, _) => UpdateProgressFill();
    }

    private void UpdateProgressFill()
    {
        if (StepProgress.Width <= 0)
            return;

        StepProgressFill.WidthRequest = StepProgress.Width * Math.Clamp(Progress, 0d, 1d);
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Step
    {
        get => (string)GetValue(StepProperty);
        set => SetValue(StepProperty, value);
    }

    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    public bool IsBackVisible
    {
        get => (bool)GetValue(IsBackVisibleProperty);
        set => SetValue(IsBackVisibleProperty, value);
    }

    private async void OnBackTapped(object? sender, EventArgs e)
    {
        if (BackRequested is not null)
        {
            BackRequested.Invoke(this, EventArgs.Empty);
            return;
        }

        var nav = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault()?.Page?.Navigation;
        if (nav?.NavigationStack.Count > 1)
            await nav.PopAsync();
    }
}
