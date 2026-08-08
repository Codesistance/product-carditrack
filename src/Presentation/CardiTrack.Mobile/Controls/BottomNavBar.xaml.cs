namespace CardiTrack.Mobile.Controls;

public enum NavTab
{
    Dashboard,
    Alerts,
    Family,
    Settings,
}

/// <summary>
/// The app's bottom navigation, drawn in XAML rather than by Shell (issue #67).
/// </summary>
/// <remarks>
/// Shell's tab bar is the platform's own, which gives no way to swap an icon on selection or
/// to carry the design's upward shadow, so <c>Shell.TabBarIsVisible</c> is false app-wide and
/// each tab page hosts one of these instead. Routes are unchanged — every tap is still a Shell
/// <c>//route</c> navigation, so back-stack and tab state behave as they did.
/// </remarks>
public partial class BottomNavBar : ContentView
{
    public static readonly BindableProperty TabProperty = BindableProperty.Create(
        nameof(Tab),
        typeof(NavTab),
        typeof(BottomNavBar),
        NavTab.Dashboard,
        propertyChanged: (bindable, _, _) => ((BottomNavBar)bindable).ApplySelection());

    /// <summary>Which tab this page sits on — drives the selected icon and label colour.</summary>
    public NavTab Tab
    {
        get => (NavTab)GetValue(TabProperty);
        set => SetValue(TabProperty, value);
    }

    public BottomNavBar()
    {
        InitializeComponent();
        ApplySelection();
    }

    private void ApplySelection()
    {
        var resources = Microsoft.Maui.Controls.Application.Current!.Resources;
        var selectedColor = (Color)resources["PrimaryDark"];
        var unselectedColor = (Color)resources["MutedText"];

        Style(DashboardIcon, DashboardLabel, "icon_tab_home", Tab == NavTab.Dashboard);
        Style(AlertsIcon, AlertsLabel, "icon_tab_alerts", Tab == NavTab.Alerts);
        Style(FamilyIcon, FamilyLabel, "icon_tab_family", Tab == NavTab.Family);
        Style(SettingsIcon, SettingsLabel, "icon_tab_settings", Tab == NavTab.Settings);

        void Style(Image icon, Label label, string iconStem, bool isSelected)
        {
            icon.Source = isSelected ? $"{iconStem}_active.svg" : $"{iconStem}.svg";
            // Figma puts a drop shadow under the selected glyph only. It lives here rather than
            // in the SVG because Resizetizer rasterises these at build time and drops filters.
            icon.Shadow = isSelected
                ? new Shadow
                {
                    Brush = new SolidColorBrush(Color.FromArgb("#1884DC")),
                    Opacity = 0.26f,
                    Radius = 2f,
                    Offset = new Point(0, 4),
                }
                : null;
            label.TextColor = isSelected ? selectedColor : unselectedColor;
        }
    }

    private void OnDashboardTapped(object? sender, TappedEventArgs e) => GoTo(NavTab.Dashboard, "//dashboard");

    private void OnAlertsTapped(object? sender, TappedEventArgs e) => GoTo(NavTab.Alerts, "//alerts");

    private void OnFamilyTapped(object? sender, TappedEventArgs e) => GoTo(NavTab.Family, "//family");

    private void OnSettingsTapped(object? sender, TappedEventArgs e) => GoTo(NavTab.Settings, "//settings");

    // Re-navigating to the tab you are already on would pop any page pushed above it, so the
    // tap is swallowed instead.
    private void GoTo(NavTab tab, string route)
    {
        if (Tab == tab)
            return;
        _ = Shell.Current.GoToAsync(route);
    }
}
