namespace CardiTrack.Mobile;

public partial class AppShell : Shell
{
    public AppShell()
    {
        InitializeComponent();

        // Pages pushed on top of a tab rather than owning one. Registered here so
        // GoToAsync("<route>?memberId=...") resolves them through DI.
        Routing.RegisterRoute(CardiMemberDetailPage.Route, typeof(CardiMemberDetailPage));
        Routing.RegisterRoute(EditCardiMemberPage.Route, typeof(EditCardiMemberPage));
        Routing.RegisterRoute(DeviceManagementPage.Route, typeof(DeviceManagementPage));
    }
}
