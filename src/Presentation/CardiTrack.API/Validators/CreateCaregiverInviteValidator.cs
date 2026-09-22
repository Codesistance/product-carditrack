using CardiTrack.Application.DTOs.Requests;
using FluentValidation;

namespace CardiTrack.API.Validators;

public class CreateCaregiverInviteValidator : AbstractValidator<CreateCaregiverInviteRequest>
{
    /// <summary>
    /// Wire values for the role a caregiver invitation grants, matching
    /// <c>CaregiverInviteService</c>. Two copies of two short strings, and deliberately not one
    /// shared constant, for the same reason <see cref="CreateDeviceInviteValidator.Channels"/> is
    /// duplicated: this is the API's contract with a shipped mobile build, and the service's is
    /// with the database. A shared constant would let both move at once, which is the change that
    /// breaks an app already in the stores.
    /// </summary>
    /// <remarks>
    /// <c>staff</c> is absent on purpose. It exists in <c>UserRole</c> for the Enterprise offering
    /// and is never assignable to a family, so a request asking for it is refused here rather than
    /// quietly downgraded to member by the service.
    /// </remarks>
    internal static readonly string[] Roles = ["member", "admin"];

    public CreateCaregiverInviteValidator()
    {
        RuleFor(x => x.Role)
            .NotEmpty().WithMessage("Role is required")
            .Must(r => Roles.Contains(r, StringComparer.OrdinalIgnoreCase))
            .WithMessage($"Role must be one of: {string.Join(", ", Roles)}");

        // Somebody who can see nothing and hear nothing has been invited to no purpose, and the
        // grant would be a row that only confuses the caregiver list.
        RuleFor(x => x)
            .Must(x => x.CanViewHealthData || x.ReceiveAlerts)
            .WithMessage("An invitation has to offer something: health data, alerts, or both.");
    }
}
