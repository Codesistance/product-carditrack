using CardiTrack.Application.DTOs.Requests;
using FluentValidation;

namespace CardiTrack.API.Validators;

public class AskMemberQuestionValidator : AbstractValidator<AskMemberQuestionRequest>
{
    /// <summary>
    /// A caregiver question is one or two sentences, not a note. The same 500-character
    /// ceiling the digest uses for a proposed question; longer than that crowds the readings
    /// out of the context window on a single CPU-served model.
    /// </summary>
    public const int MaxQuestionLength = 500;

    public AskMemberQuestionValidator()
    {
        RuleFor(x => x.Question)
            .NotEmpty().WithMessage("Type a question about the readings")
            .MaximumLength(MaxQuestionLength).WithMessage("Questions cannot exceed 500 characters");
    }
}
