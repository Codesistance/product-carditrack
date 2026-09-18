using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Services;
using Microsoft.Extensions.Logging;

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
            // Mid-flow entry: the onboarding "Step N of 4" story doesn't apply, but the
            // header still needs a second line so it matches SignIn / Alerts rather than
            // a title floating in an empty gradient band.
            Header.Step = "Building their baseline";
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

    /// <summary>
    /// The page's only exit. Nothing here may escape: this is an <c>async void</c> handler, so an
    /// exception from the hand-off would be raised on the sync context and take the app down —
    /// leaving whatever sits below us in the Android task (the OAuth browser) on screen, which is
    /// the very failure <see cref="WizardContext.GoToDashboardAsync"/> exists to prevent.
    /// </summary>
    private async void OnGoToDashboardClicked(object? sender, EventArgs e)
    {
        try
        {
            await _ctx.GoToDashboardAsync(this);
        }
        catch (Exception ex)
        {
            ServiceHelper.GetRequiredService<ILogger<BaselineLearningPage>>()
                .LogError(ex, "Go to Dashboard could not hand over to the shell.");
            AppForeground.BringToFront();
        }
    }
}
