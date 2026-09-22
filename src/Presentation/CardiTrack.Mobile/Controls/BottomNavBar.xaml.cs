namespace CardiTrack.Mobile.Controls;

public enum NavTab
{
    Dashboard,
    Alerts,
    Family,
    Journal,
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
        Style(JournalIcon, JournalLabel, "icon_tab_journal", Tab == NavTab.Journal);
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
                : new Shadow
                {
                    Opacity = 0f,
                };
            label.TextColor = isSelected ? selectedColor : unselectedColor;
        }
    }

    private void OnDashboardTapped(object? sender, TappedEventArgs e) => GoTo(NavTab.Dashboard, AppShell.DashboardRoute);

    private void OnAlertsTapped(object? sender, TappedEventArgs e) => GoTo(NavTab.Alerts, AppShell.AlertsRoute);

    private void OnFamilyTapped(object? sender, TappedEventArgs e) => GoTo(NavTab.Family, AppShell.FamilyRoute);

    private void OnSummariesTapped(object? sender, TappedEventArgs e) => GoTo(NavTab.Journal, AppShell.JournalRoute);

    private void OnSettingsTapped(object? sender, TappedEventArgs e) => GoTo(NavTab.Settings, AppShell.SettingsRoute);

    /// <summary>
    /// Raised when the tab already showing is tapped again, which the bar itself does nothing
    /// about. The Family tab uses it to open its switcher drawer (D-19), so the way to a second
    /// family is the tab a caregiver is already on.
    /// </summary>
    /// <remarks>
    /// Static because the bar is one instance per page and the page that cares is not the one
    /// holding the bar that was tapped — every page has its own. A page subscribes for its own
    /// tab and checks it is on screen before acting.
    /// </remarks>
    public static event EventHandler<NavTab>? SameTabTapped;

    /// <remarks>
    /// Sitting on a tab's own root, re-navigating to it would rebuild the page for nothing, so
    /// the tap is swallowed — but announced first, for a tab that has something to say about
    /// being tapped twice.
    /// </remarks>
    private void GoTo(NavTab tab, string route)
    {
        var isTabRoot = Shell.Current.Navigation.NavigationStack.Count <= 1;
        if (Tab == tab && isTabRoot)
        {
            SameTabTapped?.Invoke(this, tab);
            return;
        }

        // Choosing a tab ends whatever journey a content affordance had started. The bar
        // deliberately records no origin of its own (see TabNavigation), but it must cancel one
        // still pending, or a back press at the tab the caregiver just chose would return them to
        // a page they left two navigations ago and undo the tap that brought them here.
        Services.TabNavigation.Origin.Clear();

        _ = Shell.Current.GoToAsync(route);
    }
}
