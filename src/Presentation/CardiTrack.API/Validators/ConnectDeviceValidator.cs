using CardiTrack.Application.DTOs.Requests;
using FluentValidation;

namespace CardiTrack.API.Validators;

public class ConnectDeviceValidator : AbstractValidator<ConnectDeviceRequest>
{
    private static readonly string[] ServerOAuthProviders = ["fitbit", "garmin", "samsung_health", "withings"];

    public ConnectDeviceValidator()
    {
        RuleFor(x => x.Provider)
            .NotEmpty().WithMessage("Provider is required")
            .Must(p => ServerOAuthProviders.Contains(p, StringComparer.OrdinalIgnoreCase))
            .WithMessage("Provider must be one of: fitbit, garmin, samsung_health, withings");

        // A fragment can't survive the bounce: the callback params are appended to this URI,
        // and anything after a '#' would swallow them instead of arriving as query values.
        RuleFor(x => x.RedirectUri)
            .NotEmpty().WithMessage("Redirect URI is required")
            .Must(uri => Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
                         && string.IsNullOrEmpty(parsed.Fragment))
            .WithMessage("Redirect URI must be an absolute URI without a fragment");
    }
}
