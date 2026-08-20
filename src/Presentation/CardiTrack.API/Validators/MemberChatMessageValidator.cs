using CardiTrack.Application.DTOs.Requests;
using FluentValidation;

namespace CardiTrack.API.Validators;

public class MemberChatMessageValidator : AbstractValidator<MemberChatMessageRequest>
{
    public const int MaxMessageLength = 1000;

    public MemberChatMessageValidator()
    {
        RuleFor(x => x.Message)
            .NotEmpty().WithMessage("Type a message to send")
            .MaximumLength(MaxMessageLength).WithMessage($"Messages cannot exceed {MaxMessageLength} characters");
    }
}
