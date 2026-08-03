namespace CardiTrack.Mobile.Controls;

public partial class WizardHeader : ContentView
{
    public static readonly BindableProperty TitleProperty =
        BindableProperty.Create(nameof(Title), typeof(string), typeof(WizardHeader), string.Empty);

    public static readonly BindableProperty StepProperty =
        BindableProperty.Create(nameof(Step), typeof(string), typeof(WizardHeader), string.Empty);

    public static readonly BindableProperty ProgressProperty =
        BindableProperty.Create(nameof(Progress), typeof(double), typeof(WizardHeader), 0d,
            propertyChanged: (b, _, v) => ((WizardHeader)b).StepProgress.IsVisible = (double)v > 0);

    public static readonly BindableProperty IsBackVisibleProperty =
        BindableProperty.Create(nameof(IsBackVisible), typeof(bool), typeof(WizardHeader), true,
            propertyChanged: (b, _, v) => ((WizardHeader)b).BackButton.IsVisible = (bool)v);

    public event EventHandler? BackRequested;

    public WizardHeader()
    {
        InitializeComponent();
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
