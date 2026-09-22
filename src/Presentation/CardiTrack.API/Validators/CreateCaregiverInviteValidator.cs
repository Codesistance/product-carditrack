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
    /// <para>
    /// <c>staff</c> is absent on purpose. It exists in <c>UserRole</c> for the Enterprise offering
    /// and is never assignable to a family, so a request asking for it is refused here rather than
    /// quietly downgraded to member by the service.
    /// </para>
    /// <para>
    /// <c>admin</c> is absent for the same reason, and was accepted until 2026-09-22. An
    /// invitation admits as a member: a family has one admin, and handing somebody the family and
    /// its billing is its own deliberate act with the incumbent's hand on it
    /// (<c>PUT /api/v1/families/{id}/admin</c>), not a field on a message sent a week earlier.
    /// Accepting the word here and downgrading it in the service meant answering 200 to a request
    /// for a handover and then not performing one — the precise thing the paragraph above says
    /// this list exists to prevent.
    /// </para>
    /// </remarks>
    internal static readonly string[] Roles = ["member"];

    public CreateCaregiverInviteValidator()
    {
        RuleFor(x => x.Role)
            .NotEmpty().WithMessage("Role is required")
            .Must(r => Roles.Contains(r, StringComparer.OrdinalIgnoreCase))
            .WithMessage(
                "An invitation can only add someone as a member. To hand over the family, "
                + "use the admin transfer instead.");

        // Somebody who can see nothing and hear nothing has been invited to no purpose, and the
        // grant would be a row that only confuses the caregiver list.
        RuleFor(x => x)
            .Must(x => x.CanViewHealthData || x.ReceiveAlerts)
            .WithMessage("An invitation has to offer something: health data, alerts, or both.");
    }
}
