using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.Services;
using FluentValidation;

namespace CardiTrack.API.Validators;

public class HistoryRepullValidator : AbstractValidator<HistoryRepullRequest>
{
    public HistoryRepullValidator()
    {
        RuleFor(x => x.Days)
            .InclusiveBetween(1, HistoryRepullWindow.MaxDays)
            .WithMessage($"Days must be between 1 and {HistoryRepullWindow.MaxDays}");
    }
}
