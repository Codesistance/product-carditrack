using Microsoft.Maui.Controls.Shapes;

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

    /// <summary>How much larger the selected glyph's box is than its tab's own glyph box.</summary>
    private const double SelectedIconGrow = 1;

    /// <summary>
    /// How far the glyphs of the tabs not selected sit below their centred place, closing the gap
    /// to their label by the same amount, so each reads as one mark and its word rather than a
    /// glyph floating over a caption. 2 first, then 4 (picked from samples, 2026-09-26): at 2 the
    /// words still sat apart from their glyphs; 5 crowded the bell's clapper and the cog's teeth.
    /// </summary>
    private const double UnselectedIconDrop = 4;

    /// <summary>
    /// How far the selected glyph is lifted off the tabs' shared centre line. None: the pill is
    /// symmetric about the row, so the selected glyph and label sit where every other tab's do.
    /// </summary>
    private const double SelectedIconLift = 0;

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
        BarBorder.SizeChanged += (_, _) => PaintGround();
        // Subscribed once and kept, not dropped on Unloaded and taken again on Loaded. Shell
        // unloads a tab page's view when the caregiver moves two tabs away, and when that page
        // comes back its Appearing is raised before its Loaded — so a handler re-attached in
        // Loaded missed the very Appearing it was waiting for, and the bar went on showing the
        // tab it last slid to. The bar lives inside the page, so holding the page's event for the
        // page's lifetime keeps nothing alive that the page does not. Loaded runs the same check,
        // for whichever of the two arrives last.
        Loaded += (_, _) =>
        {
            if (_page is null)
            {
                _page = FindPage();
                if (_page is not null)
                    _page.Appearing += OnPageAppearing;
            }
            ResetIfReturned();
        };
    }

    /// <summary>How much of the bar's top is see-through, for the pill to rise into.</summary>
    private const double ClearStrip = 8;

    /// <summary>
    /// The bar's fill: nothing for its top <see cref="ClearStrip"/>, then TabBarBrush's white into
    /// pale blue down to the bottom of the screen. Built against the bar's height because a
    /// gradient's stops are fractions of it, and the bar's height depends on the phone's bottom
    /// inset; a hard stop at the strip's fraction is the only way to say "8 down" in them.
    /// </summary>
    private void PaintGround()
    {
        var height = BarBorder.Height;
        if (height <= ClearStrip)
            return;

        var edge = (float)(ClearStrip / height);
        var resources = Microsoft.Maui.Controls.Application.Current!.Resources;
        var stops = ((LinearGradientBrush)resources["TabBarBrush"]).GradientStops;

        var ground = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        ground.GradientStops.Add(new GradientStop(Colors.Transparent, 0));
        ground.GradientStops.Add(new GradientStop(Colors.Transparent, edge));
        foreach (var stop in stops)
            ground.GradientStops.Add(new GradientStop(stop.Color, edge + (stop.Offset * (1 - edge))));
        BarBorder.Background = ground;
    }

    private Page? FindPage()
    {
        Element? cursor = Parent;
        while (cursor is not null and not Page)
            cursor = cursor.Parent;
        return cursor as Page;
    }

    private void OnPageAppearing(object? sender, EventArgs e) => ResetIfReturned();

    private void ResetIfReturned()
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

    /// <summary>The space the pill leaves between itself and a neighbouring tab.</summary>
    private const double PillGap = 4;

    /// <summary>The pill's corner radius wherever it is not against the screen edge.</summary>
    private const double PillCornerRadius = 5;

    /// <summary>
    /// Where the pill sits under <paramref name="tab"/>: inset <see cref="PillGap"/> from each
    /// neighbour, and flush with the screen on the side that has none. A gap at the edge read as
    /// a stray strip of bar beside the first and last tabs rather than as spacing.
    /// </summary>
    private (double X, double Width) PillFrame(NavTab tab)
    {
        var index = (int)tab;
        var left = index == 0 ? 0 : PillGap;
        var right = index == TabsGrid.ColumnDefinitions.Count - 1 ? 0 : PillGap;
        return ((index * ColumnWidth) + left, ColumnWidth - left - right);
    }

    /// <summary>
    /// Square on the side that meets the screen edge, so the pill reads as anchored there rather
    /// than as a rounded shape that happens to touch it; rounded everywhere else.
    /// </summary>
    private RoundRectangle PillShape(NavTab tab)
    {
        var index = (int)tab;
        var r = PillCornerRadius;
        if (index == 0)
            return new RoundRectangle { CornerRadius = new CornerRadius(0, r, 0, r) };
        if (index == TabsGrid.ColumnDefinitions.Count - 1)
            return new RoundRectangle { CornerRadius = new CornerRadius(r, 0, r, 0) };
        return new RoundRectangle { CornerRadius = r };
    }

    /// <summary>Puts the pill under <paramref name="tab"/> with no animation — layout, and a hidden bar coming back.</summary>
    private void SnapPill(NavTab tab)
    {
        if (TabsGrid.Width <= 0)
            return;

        this.AbortAnimation("pill");
        var (x, width) = PillFrame(tab);
        SelectionPill.TranslationX = x;
        SelectionPill.WidthRequest = width;
        SelectionPill.StrokeShape = PillShape(tab);
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
            // The tabs not selected step down — the glyph by 3, the label by 1 — and the selected
            // one steps up by 1, so it stands out by more than colour. The margin gives the
            // difference back, split above and below, so every label keeps the same baseline
            // whichever tab is selected. An unselected glyph then moves down within that space
            // (more above, less below), which closes the gap to its label without moving it.
            var size = isSelected ? box + SelectedIconGrow : box - UnselectedIconShrink;
            var inset = (box == 24 ? 2 : 0)
                + (isSelected ? -SelectedIconGrow / 2 : UnselectedIconShrink / 2);
            var drop = isSelected ? 0 : UnselectedIconDrop;
            icon.WidthRequest = size;
            icon.HeightRequest = size;
            icon.Margin = new Thickness(0, inset + drop, 0, inset - drop);
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
            // doesn't show another tab selected. A quick caregiver can already be back on this
            // page by the time the navigation returns — its Appearing has come and gone with
            // nothing to reset — so then it is put back here, since no later Appearing will.
            if (_shown != Tab)
            {
                if (_page is not null && Shell.Current.CurrentPage == _page)
                    ApplySelection();
                else
                    _resetOnReturn = true;
            }
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

        // Position and width travel together: an edge tab's pill is wider than a middle one's by
        // the gap it gives up at the screen. Rounded all round while it moves — it has left the
        // edge it was squared against — and squared again only if it lands on one.
        var (startX, startWidth) = (SelectionPill.TranslationX, SelectionPill.Width > 0 ? SelectionPill.Width : SelectionPill.WidthRequest);
        var (endX, endWidth) = PillFrame(tab);
        var slide = new TaskCompletionSource();
        this.AbortAnimation("pill");
        SelectionPill.StrokeShape = new RoundRectangle { CornerRadius = PillCornerRadius };
        new Animation(v =>
            {
                SelectionPill.TranslationX = startX + ((endX - startX) * v);
                SelectionPill.WidthRequest = startWidth + ((endWidth - startWidth) * v);
            })
            .Commit(this, "pill", 16, SlideMs, Easing.CubicOut, (_, _) =>
            {
                SelectionPill.StrokeShape = PillShape(tab);
                slide.TrySetResult();
            });

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
