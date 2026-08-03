using CardiTrack.Application.DTOs.Requests;
using FluentValidation;

namespace CardiTrack.API.Validators;

public class OAuthCallbackValidator : AbstractValidator<OAuthCallbackRequest>
{
    public OAuthCallbackValidator()
    {
        RuleFor(x => x.Code).NotEmpty().WithMessage("Authorization code is required");
        RuleFor(x => x.State).NotEmpty().WithMessage("State token is required");
        RuleFor(x => x.CodeVerifier).NotEmpty().WithMessage("Code verifier is required");
    }
}
