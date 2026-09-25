using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.Services;
using FluentValidation;

namespace CardiTrack.API.Validators;

public class CreateCardiMemberValidator : AbstractValidator<CreateCardiMemberRequest>
{
    public CreateCardiMemberValidator()
    {
        // Rules on FirstName/LastName only. A legacy client that sends just Name reaches them
        // through the request's fallback getters, so it is held to exactly the same rules.
        RuleFor(x => x.FirstName)
            .NotEmpty().WithMessage("First name is required")
            .MaximumLength(100).WithMessage("First name cannot exceed 100 characters");

        RuleFor(x => x.LastName)
            .MaximumLength(100).WithMessage("Last name cannot exceed 100 characters")
            .When(x => !string.IsNullOrEmpty(x.LastName));

        RuleFor(x => x.DateOfBirth)
            .NotEmpty().WithMessage("Date of birth is required")
            .Must(BeValidAge).WithMessage("CardiMember must be at least 18 years old and not more than 120 years old");

        RuleFor(x => x.Gender)
            .IsInEnum().WithMessage("Invalid gender value");

        RuleFor(x => x.Email)
            .EmailAddress().WithMessage("Invalid email format")
            .When(x => !string.IsNullOrEmpty(x.Email));

        RuleFor(x => x.Phone)
            .Matches(@"^\+?[1-9]\d{1,14}$").WithMessage("Invalid phone number format")
            .When(x => !string.IsNullOrEmpty(x.Phone));

        RuleFor(x => x.EmergencyContactPhone)
            .Matches(@"^\+?[1-9]\d{1,14}$").WithMessage("Invalid emergency contact phone format")
            .When(x => !string.IsNullOrEmpty(x.EmergencyContactPhone));

        // Optional — see UpdateCardiMemberValidator for why 0 is accepted.
        RuleFor(x => x.RelationshipType)
            .Must(r => r == 0 || Enum.IsDefined(r)).WithMessage("Invalid relationship type");

        RuleFor(x => x.MedicalNotes)
            .MaximumLength(2000).WithMessage("Medical notes cannot exceed 2000 characters")
            .When(x => !string.IsNullOrEmpty(x.MedicalNotes));

        // Base64 shape and the 5 MB decoded cap only — whether the bytes are a real JPEG/PNG is
        // the processor's call (content sniffing), so image-ness is not re-judged here with a
        // second, weaker opinion. Custom rather than chained Musts so the payload is decoded
        // exactly once, and IsNullOrWhiteSpace so what this skips is exactly what the service
        // treats as "no photo supplied".
        RuleFor(x => x.PhotoBase64).Custom(ValidatePhotoBase64);
    }

    /// <summary>Shared by <see cref="UpdateCardiMemberValidator"/> — one photo rule, two forms.</summary>
    internal static void ValidatePhotoBase64<T>(string? value, FluentValidation.ValidationContext<T> context)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        if (!ProfilePhotoBase64.TryDecode(value, out var bytes))
            context.AddFailure("Photo must be valid base64 image data");
        else if (bytes.Length > ProfilePhotoBase64.MaxDecodedBytes)
            context.AddFailure("Photos can be at most 5 MB");
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
