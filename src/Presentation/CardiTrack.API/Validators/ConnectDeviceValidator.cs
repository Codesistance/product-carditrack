using CardiTrack.Application.DTOs.Requests;
using FluentValidation;

namespace CardiTrack.API.Validators;

public class ConnectDeviceValidator : AbstractValidator<ConnectDeviceRequest>
{
    private static readonly string[] ServerOAuthProviders =
        ["fitbit", "pixel_watch", "garmin", "samsung_health", "withings"];

    public ConnectDeviceValidator()
    {
        RuleFor(x => x.Provider)
            .NotEmpty().WithMessage("Provider is required")
            .Must(p => ServerOAuthProviders.Contains(p, StringComparer.OrdinalIgnoreCase))
            .WithMessage("Provider must be one of: fitbit, pixel_watch, garmin, samsung_health, withings");

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

        RuleFor(x => x.Mode)
            .Must(m => m is null || Modes.Contains(m, StringComparer.OrdinalIgnoreCase))
            .WithMessage("Mode must be one of: add, reconnect, replace");

        // Reconnect and replace act on one named connection; add acts on none, and a device id
        // sent with it would be a client that believed it was doing something else.
        RuleFor(x => x.DeviceId)
            .NotNull()
            .When(x => !IsAdd(x.Mode))
            .WithMessage("Device id is required to reconnect or replace a device");
        RuleFor(x => x.DeviceId)
            .Null()
            .When(x => IsAdd(x.Mode))
            .WithMessage("Device id must not be sent when adding a device");
    }

    private static readonly string[] Modes =
        [ConnectDeviceRequest.ModeAdd, ConnectDeviceRequest.ModeReconnect, ConnectDeviceRequest.ModeReplace];

    private static bool IsAdd(string? mode) =>
        mode is null || string.Equals(mode, ConnectDeviceRequest.ModeAdd, StringComparison.OrdinalIgnoreCase);

    private static bool IsAppDeepLink(string? uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
        && string.Equals(parsed.Scheme, ConnectDeviceRequest.AppRedirectScheme, StringComparison.OrdinalIgnoreCase)
        && string.IsNullOrEmpty(parsed.Fragment);
}
