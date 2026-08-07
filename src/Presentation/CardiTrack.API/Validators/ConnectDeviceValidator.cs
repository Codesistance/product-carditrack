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

        // Two things the bounce endpoint relies on, checked here so a bad value fails fast
        // instead of being cached and only breaking once the provider redirects back:
        //   - the app scheme, since the bounce forwards into whatever was cached;
        //   - no fragment, because the callback params are appended to this URI and anything
        //     after a '#' would swallow them instead of arriving as query values.
        // "Absolute" alone is not enough: on Linux Uri.TryCreate accepts a bare path like
        // "/oauth/callback" as an absolute file: URI, so it would pass in the deployed API.
        RuleFor(x => x.RedirectUri)
            .NotEmpty().WithMessage("Redirect URI is required")
            .Must(IsAppDeepLink)
            .WithMessage($"Redirect URI must be a {ConnectDeviceRequest.AppRedirectScheme}:// URI without a fragment");
    }

    private static bool IsAppDeepLink(string? uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
        && string.Equals(parsed.Scheme, ConnectDeviceRequest.AppRedirectScheme, StringComparison.OrdinalIgnoreCase)
        && string.IsNullOrEmpty(parsed.Fragment);
}
