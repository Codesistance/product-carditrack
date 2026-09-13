namespace CardiTrack.Mobile.Controls;

public partial class WizardHeader : ContentView
{
    public static readonly BindableProperty TitleProperty =
        BindableProperty.Create(nameof(Title), typeof(string), typeof(WizardHeader), string.Empty,
            propertyChanged: (b, _, _) => ((WizardHeader)b).ApplyIcon());

    public static readonly BindableProperty StepProperty =
        BindableProperty.Create(nameof(Step), typeof(string), typeof(WizardHeader), string.Empty,
            propertyChanged: (b, _, _) => ((WizardHeader)b).ApplyStepVisibility());

    public static readonly BindableProperty ProgressProperty =
        BindableProperty.Create(nameof(Progress), typeof(double), typeof(WizardHeader), 0d,
            propertyChanged: (b, _, v) =>
            {
                var header = (WizardHeader)b;
                if (header.StepProgress is null)
                    return;
                header.StepProgress.IsVisible = (double)v > 0;
                header.UpdateProgressFill();

                var percent = (int)Math.Round(Math.Clamp((double)v, 0d, 1d) * 100);
                SemanticProperties.SetDescription(header.StepProgress, $"Wizard progress: {percent} percent");
            });

    public static readonly BindableProperty IsBackVisibleProperty =
        BindableProperty.Create(nameof(IsBackVisible), typeof(bool), typeof(WizardHeader), true,
            propertyChanged: (b, _, _) => ((WizardHeader)b).ApplyIcon());

    /// <summary>
    /// Glyph in the left circle when <see cref="IsBackVisible"/> is false. Terminal steps
    /// still show the circle — every other header in the app does — they just do not
    /// offer a back action. Ignored while back is shown.
    /// </summary>
    public static readonly BindableProperty IconSourceProperty =
        BindableProperty.Create(nameof(IconSource), typeof(string), typeof(WizardHeader),
            "icon_home_white.svg",
            propertyChanged: (b, _, _) => ((WizardHeader)b).ApplyIcon());

    /// <summary>
    /// Inset inside the gradient band. Defaults to the status-bar-clearing padding the
    /// in-scroller wizard pages still use. Pages that claim the top safe-area themselves
    /// pass the compact Alerts/Dashboard inset so this chrome matches the rest of the app.
    /// </summary>
    public static readonly BindableProperty ContentPaddingProperty =
        BindableProperty.Create(nameof(ContentPadding), typeof(Thickness), typeof(WizardHeader),
            new Thickness(20, 48, 20, 24));

    public event EventHandler? BackRequested;

    public WizardHeader()
    {
        InitializeComponent();
        StepProgress.SizeChanged += (_, _) => UpdateProgressFill();
        ApplyStepVisibility();
        ApplyIcon();
    }

    private void UpdateProgressFill()
    {
        if (StepProgress.Width <= 0)
            return;

        StepProgressFill.WidthRequest = StepProgress.Width * Math.Clamp(Progress, 0d, 1d);
    }

    private void ApplyStepVisibility()
    {
        if (StepLabel is null)
            return;
        StepLabel.IsVisible = !string.IsNullOrWhiteSpace(Step);
    }

    private void ApplyIcon()
    {
        if (HeaderIcon is null || HeaderIconImage is null)
            return;

        if (IsBackVisible)
        {
            HeaderIconImage.Source = "icon_back_white.svg";
            HeaderIcon.InputTransparent = false;
            AutomationProperties.SetIsInAccessibleTree(HeaderIcon, true);
            SemanticProperties.SetDescription(HeaderIcon, "Go back");
            return;
        }

        HeaderIconImage.Source = string.IsNullOrWhiteSpace(IconSource) ? "icon_home_white.svg" : IconSource;
        HeaderIcon.InputTransparent = true;
        // Decorative: the title next to it is already announced. A second copy of
        // the heading on a non-actionable circle is noise for TalkBack / VoiceOver.
        AutomationProperties.SetIsInAccessibleTree(HeaderIcon, false);
        SemanticProperties.SetDescription(HeaderIcon, null);
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

    public string IconSource
    {
        get => (string)GetValue(IconSourceProperty);
        set => SetValue(IconSourceProperty, value);
    }

    public Thickness ContentPadding
    {
        get => (Thickness)GetValue(ContentPaddingProperty);
        set => SetValue(ContentPaddingProperty, value);
    }

    private async void OnBackTapped(object? sender, EventArgs e)
    {
        if (!IsBackVisible)
            return;

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
