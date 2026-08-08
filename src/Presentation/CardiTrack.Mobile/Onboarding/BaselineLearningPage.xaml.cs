using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Services;

namespace CardiTrack.Mobile.Onboarding;

/// <summary>M1-08: baseline-learning explainer; the last onboarding step before the dashboard.</summary>
public partial class BaselineLearningPage : ContentPage
{
    private readonly ICardiTrackApiClient _api;
    private readonly WizardContext _ctx;
    private readonly CardiMemberResponse _member;

    public BaselineLearningPage(WizardContext ctx)
    {
        InitializeComponent();
        _api = ServiceHelper.GetRequiredService<ICardiTrackApiClient>();
        _ctx = ctx;
        _member = ctx.RequireMember();
        TitleLabel.Text = $"Getting to know {_member.Name}";
        IntroLabel.Text = $"Over the next 30 days, CardiTrack will learn what a normal day looks like for {_member.Name}:";

        if (ctx.Origin == WizardOrigin.Modal)
        {
            // Mid-flow entry: the onboarding "Step N of 4" story doesn't apply.
            Header.Step = string.Empty;
            Header.Progress = 0;
        }
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        try
        {
            var dashboard = await _api.GetDashboardAsync(_member.Id);
            var baseline = dashboard.Baseline;
            var day = Math.Max(1, baseline.DaysCaptured);
            DayLabel.Text = $"Day {day} of {baseline.DaysRequired}";
            PercentLabel.Text = $"{baseline.PercentComplete}% Complete";
            LearningProgress.Progress = Math.Clamp(baseline.PercentComplete / 100d, 0.01, 1);
        }
        catch (ApiException)
        {
            // Keep the Day 1 defaults; the dashboard shows live progress from here on.
        }
    }

    private async void OnGoToDashboardClicked(object? sender, EventArgs e) =>
        await _ctx.GoToDashboardAsync(this);

    private async void OnInviteFamilyTapped(object? sender, EventArgs e)
    {
        await ServiceHelper.GetRequiredService<IPopupService>().ShowInfoAsync(
            "Family invitations arrive with the next release. You can invite your family from the Family tab once it's live.",
            "Coming soon");
    }
}
