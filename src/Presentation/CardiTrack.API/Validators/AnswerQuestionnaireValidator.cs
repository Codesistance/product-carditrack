using CardiTrack.Application.DTOs.Requests;
using FluentValidation;

namespace CardiTrack.API.Validators;

public class AnswerQuestionnaireValidator : AbstractValidator<AnswerQuestionnaireRequest>
{
    public AnswerQuestionnaireValidator()
    {
        // An empty answer is a dismissal wearing the wrong endpoint — there is a dismiss action
        // for skipping, and storing blank text would put an answered question with nothing in it
        // in front of the model.
        RuleFor(x => x.AnswerText)
            .NotEmpty().WithMessage("Type an answer, or skip the question instead")
            .MaximumLength(2000).WithMessage("Answers cannot exceed 2000 characters");
    }
}
