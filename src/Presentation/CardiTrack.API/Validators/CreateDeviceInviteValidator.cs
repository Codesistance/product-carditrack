using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.Services;
using FluentValidation;

namespace CardiTrack.API.Validators;

public class CreateDeviceInviteValidator : AbstractValidator<CreateDeviceInviteRequest>
{
    /// <summary>
    /// Wire values for the delivery channel, matching <c>DeviceConnectionInviteService</c>. Two
    /// copies of two short strings, and deliberately not one shared constant: this is the API's
    /// contract with a shipped mobile build, and the service's is with the database. The parity test
    /// is what keeps them honest — a shared constant would let both move at once, which is the
    /// change that breaks an app already in the stores.
    /// </summary>
    internal static readonly string[] Channels = ["link", "qr"];

    public CreateDeviceInviteValidator()
    {
        RuleFor(x => x.Provider)
            .NotEmpty().WithMessage("Provider is required")
            .Must(p => DeviceProviderNames.TryResolve(p, out _))
            .WithMessage($"Provider must be one of: {string.Join(", ", DeviceProviderNames.All)}");

        RuleFor(x => x.Channel)
            .NotEmpty().WithMessage("Channel is required")
            .Must(c => Channels.Contains(c, StringComparer.OrdinalIgnoreCase))
            .WithMessage($"Channel must be one of: {string.Join(", ", Channels)}");
    }
}
