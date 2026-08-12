using CardiTrack.Application.DTOs.Requests;
using FluentValidation;

namespace CardiTrack.API.Validators;

/// <summary>
/// M1-14 edit form. Mirrors <see cref="CreateCardiMemberValidator"/> so a member cannot be
/// edited into a state they could never have been created in.
/// </summary>
public class UpdateCardiMemberValidator : AbstractValidator<UpdateCardiMemberRequest>
{
    public UpdateCardiMemberValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Name is required")
            .Length(2, 100).WithMessage("Name must be between 2 and 100 characters");

        RuleFor(x => x.DateOfBirth)
            .NotEmpty().WithMessage("Date of birth is required")
            .Must(BeValidAge).WithMessage("CardiMember must be at least 18 years old and not more than 120 years old");

        // Optional. 0 is not a member of the enum — it is what an omitted relationship
        // deserialises to for a caller that predates the DTO default — and is accepted as
        // "not stated"; CardiMemberService normalises it to Other before it is stored.
        // Anything else undefined is still a client bug worth reporting.
        RuleFor(x => x.RelationshipType)
            .Must(r => r == 0 || Enum.IsDefined(r)).WithMessage("Invalid relationship type");

        RuleFor(x => x.AlertSensitivity)
            .IsInEnum().WithMessage("Invalid alert sensitivity");

        RuleFor(x => x.Email)
            .EmailAddress().WithMessage("Invalid email format")
            .When(x => !string.IsNullOrEmpty(x.Email));

        RuleFor(x => x.Phone)
            .Matches(@"^\+?[1-9]\d{1,14}$").WithMessage("Invalid phone number format")
            .When(x => !string.IsNullOrEmpty(x.Phone));

        RuleFor(x => x.EmergencyContactName)
            .MaximumLength(100).WithMessage("Emergency contact name cannot exceed 100 characters")
            .When(x => !string.IsNullOrEmpty(x.EmergencyContactName));

        RuleFor(x => x.EmergencyContactPhone)
            .Matches(@"^\+?[1-9]\d{1,14}$").WithMessage("Invalid emergency contact phone format")
            .When(x => !string.IsNullOrEmpty(x.EmergencyContactPhone));

        RuleFor(x => x.MedicalNotes)
            .MaximumLength(2000).WithMessage("Medical notes cannot exceed 2000 characters")
            .When(x => !string.IsNullOrEmpty(x.MedicalNotes));
    }

    private bool BeValidAge(DateOnly dob)
    {
        var age = DateTime.UtcNow.Year - dob.Year;
        if (DateTime.UtcNow.Month < dob.Month || (DateTime.UtcNow.Month == dob.Month && DateTime.UtcNow.Day < dob.Day))
        {
            age--;
        }
        return age >= 18 && age <= 120;
    }
}
