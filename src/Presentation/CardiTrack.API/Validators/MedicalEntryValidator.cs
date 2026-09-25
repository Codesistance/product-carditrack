using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.Services;
using FluentValidation;

namespace CardiTrack.API.Validators;

public class MedicalEntryValidator : AbstractValidator<MedicalEntryRequest>
{
    public MedicalEntryValidator()
    {
        RuleFor(x => x.Kind)
            .IsInEnum().WithMessage("Choose what this is: a condition, allergy, medication or other");

        RuleFor(x => x.Text)
            .NotEmpty().WithMessage("Write what should be on file")
            .MaximumLength(MedicalLedger.MaxEntryLength)
            .WithMessage($"Keep each line to {MedicalLedger.MaxEntryLength} characters or fewer — add another for the rest");
    }
}
