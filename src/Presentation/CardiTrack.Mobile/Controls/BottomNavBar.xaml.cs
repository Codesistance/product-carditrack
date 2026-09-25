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
    private const double SelectedIconScale = 1.25;

    /// <summary>How much smaller the glyphs of the tabs not selected are drawn.</summary>
    private const double UnselectedIconShrink = 3;

    /// <summary>How far the selected glyph is lifted, so it rises out of the top of the pill.</summary>
    private const double SelectedIconLift = -7;

    private bool _navigating;

    /// <summary>The tab this bar is drawing as selected right now — see <see cref="ApplySelection(NavTab, bool)"/>.</summary>
    private NavTab _shown;

    /// <summary>
    /// This bar left its own tab for another and still shows that one. Put back when its page
    /// next appears, not the moment the navigation returns: Shell's tab switch finishes drawing
    /// after <c>GoToAsync</c> completes, and resetting then showed the pill jump back to where it
    /// came from on the page still on screen before the new page covered it.
    /// </summary>
    private bool _resetOnReturn;

    private Page? _page;

    public BottomNavBar()
    {
        InitializeComponent();
        ApplySelection();
        TabsGrid.SizeChanged += (_, _) => SnapPill(_shown);
        Loaded += (_, _) =>
        {
            _page = FindPage();
            if (_page is not null)
                _page.Appearing += OnPageAppearing;
        };
        Unloaded += (_, _) =>
        {
            if (_page is not null)
                _page.Appearing -= OnPageAppearing;
            _page = null;
        };
    }

    private Page? FindPage()
    {
        Element? cursor = Parent;
        while (cursor is not null and not Page)
            cursor = cursor.Parent;
        return cursor as Page;
    }

    private void OnPageAppearing(object? sender, EventArgs e)
    {
        if (!_resetOnReturn)
            return;
        _resetOnReturn = false;
        ApplySelection();
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
        _shown = shown;
        var resources = Microsoft.Maui.Controls.Application.Current!.Resources;
        var selectedColor = (Color)resources["PrimaryDark"];
        var unselectedColor = (Color)resources["MutedText"];

        // Two glyph boxes, as the XAML explains: the Figma pair in a 28 box, the others in 24.
        Style(DashboardIcon, DashboardLabel, "icon_tab_home", 28, shown == NavTab.Dashboard);
        Style(AlertsIcon, AlertsLabel, "icon_tab_alerts", 28, shown == NavTab.Alerts);
        Style(FamilyIcon, FamilyLabel, "icon_tab_family", 24, shown == NavTab.Family);
        Style(JournalIcon, JournalLabel, "icon_tab_journal", 24, shown == NavTab.Journal);
        Style(SettingsIcon, SettingsLabel, "icon_tab_settings", 24, shown == NavTab.Settings);
        if (snapPill)
            SnapPill(shown);

        void Style(Image icon, Label label, string iconStem, double box, bool isSelected)
        {
            // The tabs not selected step down — the glyph by 2, the label by 1 — so the selected
            // one stands out by more than colour. The margin gives the 2 back, split above and
            // below, so every label keeps the same baseline whichever tab is selected.
            var size = isSelected ? box : box - UnselectedIconShrink;
            var inset = (box == 24 ? 2 : 0) + (isSelected ? 0 : UnselectedIconShrink / 2);
            icon.WidthRequest = size;
            icon.HeightRequest = size;
            icon.Margin = new Thickness(0, inset);
            label.FontSize = isSelected ? 12 : 11;

            icon.Scale = isSelected ? SelectedIconScale : 1;
            icon.TranslationY = isSelected ? SelectedIconLift : 0;
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

            // This bar stays on a page that is now hidden (Shell keeps tab pages). It is put back
            // as its own tab when that page appears again (see _resetOnReturn), so coming back
            // doesn't show another tab selected.
            _resetOnReturn = _shown != Tab;
        }
        catch (Exception ex)
        {
            // The navigation never happened: the page is still this one, so its own tab goes
            // straight back. Logged, not rethrown — this is async void, where a rethrow ends the
            // app rather than reaching any caller.
            ApplySelection();
            Services.ScreenRefresh.LogFailure(ex, nameof(BottomNavBar), "while switching tab");
        }
        finally
        {
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
        leaving.TranslationY = SelectedIconLift;
        arriving.Scale = 1;
        arriving.TranslationY = 0;

        var start = SelectionPill.TranslationX;
        var end = (int)tab * ColumnWidth;
        var slide = new TaskCompletionSource();
        this.AbortAnimation("pill");
        new Animation(v => SelectionPill.TranslationX = v, start, end)
            .Commit(this, "pill", 16, SlideMs, Easing.CubicOut, (_, _) => slide.TrySetResult());

        await Task.WhenAll(
            slide.Task,
            leaving.ScaleToAsync(1, SlideMs, Easing.CubicOut),
            leaving.TranslateToAsync(0, 0, SlideMs, Easing.CubicOut),
            arriving.TranslateToAsync(0, SelectedIconLift, SlideMs, Easing.CubicOut),
            MagnifyAsync(arriving));
    }

    private static async Task MagnifyAsync(Image icon)
    {
        await icon.ScaleToAsync(1.4, SlideMs / 2, Easing.CubicOut);
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
