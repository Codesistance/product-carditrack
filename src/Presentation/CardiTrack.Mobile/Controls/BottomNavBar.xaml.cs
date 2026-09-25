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

    /// <summary>How long the pill takes to slide to a tapped tab, and the navigation waits for it.</summary>
    private const uint SlideMs = 240;

    /// <summary>The selected glyph's size against the others — the magnification.</summary>
    private const double SelectedIconScale = 1.15;

    private bool _navigating;

    public BottomNavBar()
    {
        InitializeComponent();
        ApplySelection();
        TabsGrid.SizeChanged += (_, _) => SnapPill(Tab);
    }

    private Image IconFor(NavTab tab) => tab switch
    {
        NavTab.Alerts => AlertsIcon,
        NavTab.Family => FamilyIcon,
        NavTab.Journal => JournalIcon,
        NavTab.Settings => SettingsIcon,
        _ => DashboardIcon,
    };

    /// <summary>One column's width: the pill moves in whole columns.</summary>
    private double ColumnWidth => TabsGrid.Width / TabsGrid.ColumnDefinitions.Count;

    /// <summary>Puts the pill under <paramref name="tab"/> with no animation — layout, and a hidden bar coming back.</summary>
    private void SnapPill(NavTab tab)
    {
        if (TabsGrid.Width <= 0)
            return;

        this.AbortAnimation("pill");
        SelectionPill.TranslationX = (int)tab * ColumnWidth;
    }

    private void ApplySelection() => ApplySelection(Tab);

    /// <param name="shown">
    /// The tab to draw as selected — <see cref="Tab"/>, except for the moment between a tap and
    /// the navigation it starts, when this bar already shows the tab it is on its way to.
    /// </param>
    private void ApplySelection(NavTab shown, bool snapPill = true)
    {
        var resources = Microsoft.Maui.Controls.Application.Current!.Resources;
        var selectedColor = (Color)resources["PrimaryDark"];
        var unselectedColor = (Color)resources["MutedText"];

        Style(DashboardIcon, DashboardLabel, "icon_tab_home", shown == NavTab.Dashboard);
        Style(AlertsIcon, AlertsLabel, "icon_tab_alerts", shown == NavTab.Alerts);
        Style(FamilyIcon, FamilyLabel, "icon_tab_family", shown == NavTab.Family);
        Style(JournalIcon, JournalLabel, "icon_tab_journal", shown == NavTab.Journal);
        Style(SettingsIcon, SettingsLabel, "icon_tab_settings", shown == NavTab.Settings);
        if (snapPill)
            SnapPill(shown);

        void Style(Image icon, Label label, string iconStem, bool isSelected)
        {
            icon.Scale = isSelected ? SelectedIconScale : 1;
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
    private async void GoTo(NavTab tab, string route)
    {
        var isTabRoot = Shell.Current.Navigation.NavigationStack.Count <= 1;
        if (Tab == tab && isTabRoot)
        {
            SameTabTapped?.Invoke(this, tab);
            return;
        }

        // A second tap while the pill is still travelling would start a second navigation.
        if (_navigating)
            return;
        _navigating = true;

        // Choosing a tab ends whatever journey a content affordance had started. The bar
        // deliberately records no origin of its own (see TabNavigation), but it must cancel one
        // still pending, or a back press at the tab the caregiver just chose would return them to
        // a page they left two navigations ago and undo the tap that brought them here.
        Services.TabNavigation.Origin.Clear();

        try
        {
            Tick();

            // Each tab page carries its own bar, so the slide can only be seen on this one: it
            // plays here first and the page changes once it lands, where the new page's bar
            // already draws its pill in the same place. A tap on the tab already selected (from
            // a page deeper in its stack) has nowhere to slide, so it goes straight away.
            if (tab != Tab)
                await SlideToAsync(tab);

            await Shell.Current.GoToAsync(route);
        }
        finally
        {
            // This bar stays on a page that is now hidden (Shell keeps tab pages). Put it back
            // as its own tab, so coming back to the page doesn't show another tab selected.
            ApplySelection();
            _navigating = false;
        }
    }

    /// <summary>
    /// Slides the pill under <paramref name="tab"/> and magnifies its glyph — overshooting, then
    /// settling at <see cref="SelectedIconScale"/> — while the one it leaves shrinks back.
    /// </summary>
    private async Task SlideToAsync(NavTab tab)
    {
        var from = Tab;
        ApplySelection(tab, snapPill: false);

        var leaving = IconFor(from);
        var arriving = IconFor(tab);
        leaving.Scale = SelectedIconScale;
        arriving.Scale = 1;

        var start = SelectionPill.TranslationX;
        var end = (int)tab * ColumnWidth;
        var slide = new TaskCompletionSource();
        this.AbortAnimation("pill");
        new Animation(v => SelectionPill.TranslationX = v, start, end)
            .Commit(this, "pill", 16, SlideMs, Easing.CubicOut, (_, _) => slide.TrySetResult());

        await Task.WhenAll(
            slide.Task,
            leaving.ScaleToAsync(1, SlideMs, Easing.CubicOut),
            MagnifyAsync(arriving));
    }

    private static async Task MagnifyAsync(Image icon)
    {
        await icon.ScaleToAsync(1.3, SlideMs / 2, Easing.CubicOut);
        await icon.ScaleToAsync(SelectedIconScale, SlideMs / 2, Easing.SpringOut);
    }

    /// <summary>The light tick a tap gives. Best-effort: a device without haptics just doesn't.</summary>
    private static void Tick()
    {
        try
        {
            HapticFeedback.Default.Perform(HapticFeedbackType.Click);
        }
        catch (Exception ex) when (ex is FeatureNotSupportedException or PermissionException)
        {
        }
    }
}
