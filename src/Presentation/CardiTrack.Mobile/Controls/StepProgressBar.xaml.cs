namespace CardiTrack.Mobile.Controls;

/// <summary>
/// The signup wizard's progress pill. Fill is a fraction of the measured track, never a
/// fixed width, so a quarter is a quarter on every screen size.
/// </summary>
public partial class StepProgressBar : ContentView
{
    public static readonly BindableProperty ProgressProperty =
        BindableProperty.Create(nameof(Progress), typeof(double), typeof(StepProgressBar), 0d,
            propertyChanged: (b, _, _) => ((StepProgressBar)b).Apply());

    public StepProgressBar()
    {
        InitializeComponent();
        Track.SizeChanged += (_, _) => UpdateFill();
        Apply();
    }

    /// <summary>How far through the wizard, 0 to 1. At 0 the bar hides itself.</summary>
    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    private void Apply()
    {
        if (Track is null)
            return;

        IsVisible = Progress > 0;
        UpdateFill();

        var percent = (int)Math.Round(Math.Clamp(Progress, 0d, 1d) * 100);
        SemanticProperties.SetDescription(this, $"Wizard progress: {percent} percent");
    }

    private void UpdateFill()
    {
        if (Track.Width <= 0)
            return;

        Fill.WidthRequest = Track.Width * Math.Clamp(Progress, 0d, 1d);
    }
}
