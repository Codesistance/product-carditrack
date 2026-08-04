using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Domain.Enums;
using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Core.Auth;
using CardiTrack.Mobile.Services;
using Microsoft.Maui.Controls.Shapes;

namespace CardiTrack.Mobile.Onboarding;

public partial class AccountSetupPage : ContentPage
{
    private readonly ICardiTrackApiClient _api;
    private readonly IAuthService _authService;
    private readonly PostLoginRouter _router;

    private OrganizationType? _selectedType;

    public AccountSetupPage()
    {
        InitializeComponent();
        _api = ServiceHelper.GetRequiredService<ICardiTrackApiClient>();
        _authService = ServiceHelper.GetRequiredService<IAuthService>();
        _router = ServiceHelper.GetRequiredService<PostLoginRouter>();
    }

    private void OnFamilyTapped(object? sender, EventArgs e) => Select(OrganizationType.Family);

    private void OnBusinessTapped(object? sender, EventArgs e) => Select(OrganizationType.Business);

    private void Select(OrganizationType type)
    {
        _selectedType = type;
        ApplyOptionState(FamilyCard, FamilyRing, FamilyDot, type == OrganizationType.Family);
        ApplyOptionState(BusinessCard, BusinessRing, BusinessDot, type == OrganizationType.Business);
        OrgNameSection.IsVisible = type == OrganizationType.Business;
        ContinueBtn.IsEnabled = true;
    }

    private static void ApplyOptionState(Border card, Ellipse ring, Ellipse dot, bool selected)
    {
        var resources = App.Current!.Resources;
        var targetStroke = (Color)resources[selected ? "Primary" : "InputBorder"];
        var targetBackground = (Color)resources[selected ? "SelectedOptionBackground" : "White"];

        var fromStroke = (card.Stroke as SolidColorBrush)?.Color ?? (Color)resources["InputBorder"];
        var fromBackground = card.BackgroundColor ?? (Color)resources["White"];

        card.AbortAnimation("optionColors");
        card.Animate("optionColors", t =>
        {
            var progress = (float)t;
            var strokeBrush = new SolidColorBrush(LerpColor(fromStroke, targetStroke, progress));
            card.Stroke = strokeBrush;
            ring.Stroke = strokeBrush;
            card.BackgroundColor = LerpColor(fromBackground, targetBackground, progress);
        }, length: 180, easing: Easing.CubicOut);

        if (selected)
        {
            _ = dot.FadeToAsync(1, 180, Easing.CubicOut);
            _ = dot.ScaleToAsync(1, 180, Easing.SpringOut);
        }
        else
        {
            _ = dot.FadeToAsync(0, 140, Easing.CubicOut);
            _ = dot.ScaleToAsync(0.5, 140, Easing.CubicOut);
        }
    }

    private static Color LerpColor(Color from, Color to, float t) => Color.FromRgba(
        from.Red + (to.Red - from.Red) * t,
        from.Green + (to.Green - from.Green) * t,
        from.Blue + (to.Blue - from.Blue) * t,
        from.Alpha + (to.Alpha - from.Alpha) * t);

    private async void OnContinueClicked(object? sender, EventArgs e)
    {
        if (_selectedType is not { } type)
            return;

        var orgName = type == OrganizationType.Business
            ? OrgNameEntry.Text?.Trim()
            : FamilyOrgName();

        if (string.IsNullOrWhiteSpace(orgName) || orgName.Length < 2)
        {
            OrgNameError.Text = "Organization name is required";
            OrgNameError.IsVisible = true;
            return;
        }
        OrgNameError.IsVisible = false;
        SetupError.IsVisible = false;

        ContinueBtn.Text = "Setting up...";
        ContinueBtn.IsEnabled = false;

        try
        {
            var organization = await _api.CreateOrganizationAsync(new CreateOrganizationRequest
            {
                Name = orgName,
                Type = type,
            });

            await _api.CreateUserAsync(new CreateUserRequest
            {
                Email = _authService.CurrentUserEmail ?? string.Empty,
                Name = _authService.CurrentUserName ?? orgName,
                OrganizationId = organization.Id,
                Role = type == OrganizationType.Business ? UserRole.Admin : UserRole.Member,
                TimeZoneId = TimeZoneInfo.Local.Id,
            });

            await _router.RouteAsync(this);
        }
        catch (ApiException ex)
        {
            SetupError.Text = ex.Message;
            SetupError.IsVisible = true;
        }
        finally
        {
            ContinueBtn.Text = "Continue";
            ContinueBtn.IsEnabled = true;
        }
    }

    private string FamilyOrgName()
    {
        var name = _authService.CurrentUserName;
        var firstName = string.IsNullOrWhiteSpace(name) ? null : name.Split(' ')[0];
        return firstName is null ? "My Family" : $"{firstName}'s Family";
    }
}
