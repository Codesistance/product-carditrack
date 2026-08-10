namespace CardiTrack.Mobile;

public partial class AppShell : Shell
{
    /// <summary>
    /// Absolute routes for the tabs declared in AppShell.xaml. A control that names where it
    /// is going — "Go to Dashboard" — navigates to one of these: the route selects the tab and
    /// drops whatever sits above it, so the user arrives at the page the button promised. ".."
    /// and modal pops only unwind one step of history, which lands wherever the user happened
    /// to come from and is only the dashboard by coincidence.
    /// </summary>
    public const string DashboardRoute = "//dashboard";

    public const string AlertsRoute = "//alerts";

    public const string FamilyRoute = "//family";

    public const string SettingsRoute = "//settings";

    public AppShell()
    {
        InitializeComponent();

        // Pages pushed on top of a tab rather than owning one. Registered here so
        // GoToAsync("<route>?memberId=...") resolves them through DI.
        Routing.RegisterRoute(CardiMemberDetailPage.Route, typeof(CardiMemberDetailPage));
        Routing.RegisterRoute(EditCardiMemberPage.Route, typeof(EditCardiMemberPage));
        Routing.RegisterRoute(DeviceManagementPage.Route, typeof(DeviceManagementPage));
        Routing.RegisterRoute(NotificationsPage.Route, typeof(NotificationsPage));
    }
}
