using CardiTrack.Application.DTOs.Requests;
using FluentValidation;

namespace CardiTrack.API.Validators;

public class OfferStandingFactValidator : AbstractValidator<OfferStandingFactRequest>
{
    public OfferStandingFactValidator()
    {
        RuleFor(x => x.FactText)
            .NotEmpty().WithMessage("Tell us something we should know")
            .MaximumLength(2000).WithMessage("Notes cannot exceed 2000 characters");
    }
}
