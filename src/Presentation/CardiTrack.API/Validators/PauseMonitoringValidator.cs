using CardiTrack.Application.DTOs.Requests;
using FluentValidation;

namespace CardiTrack.API.Validators;

public class PauseMonitoringValidator : AbstractValidator<PauseMonitoringRequest>
{
    public PauseMonitoringValidator()
    {
        // Bounded on both ends: a pause someone forgets about is how a monitored person
        // stops being monitored without anyone deciding to.
        RuleFor(x => x.DurationHours)
            .InclusiveBetween(1, 168).WithMessage("Choose a pause between 1 hour and 7 days");

        RuleFor(x => x.Reason)
            .MaximumLength(200).WithMessage("Reason cannot exceed 200 characters")
            .When(x => !string.IsNullOrEmpty(x.Reason));
    }
}
