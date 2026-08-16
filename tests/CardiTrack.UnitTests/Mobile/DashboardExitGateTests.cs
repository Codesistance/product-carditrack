using CardiTrack.Mobile.Core.Navigation;

namespace CardiTrack.UnitTests.Mobile;

public class DashboardExitGateTests
{
    [Theory]
    [InlineData("//dashboard")]
    [InlineData("//dashboard/")]
    [InlineData("//Dashboard")]
    public void Dashboard_tab_root_is_the_gate(string location) =>
        Assert.True(DashboardExitGate.IsDashboardTabRoot(location, navigationStackCount: 1, modalStackCount: 0));

    [Theory]
    [InlineData("//dashboard/memberdetail")]
    [InlineData("//alerts")]
    [InlineData("")]
    [InlineData(null)]
    public void A_page_that_is_not_the_dashboard_root_is_not_the_gate(string? location) =>
        Assert.False(DashboardExitGate.IsDashboardTabRoot(location, navigationStackCount: 1, modalStackCount: 0));

    [Fact]
    public void A_page_pushed_above_the_dashboard_is_not_the_gate() =>
        Assert.False(DashboardExitGate.IsDashboardTabRoot("//dashboard", navigationStackCount: 2, modalStackCount: 0));

    [Fact]
    public void A_modal_over_the_dashboard_is_not_the_gate() =>
        Assert.False(DashboardExitGate.IsDashboardTabRoot("//dashboard", navigationStackCount: 1, modalStackCount: 1));
}
