using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.Services;
using FluentValidation;

namespace CardiTrack.API.Validators;

/// <summary>
/// Wraps <see cref="MetricAlarmValidation"/>, which holds the actual rules. The rules live in the
/// Application layer rather than here because three callers need the same judgement — this
/// validator, the service that saves the row, and the tests that pin the boundaries — and because
/// each rule's reason belongs next to the catalogue that supplies its numbers.
/// </summary>
public class SaveMetricAlarmValidator : AbstractValidator<SaveMetricAlarmRequest>
{
    public SaveMetricAlarmValidator()
    {
        RuleFor(x => x).Custom((request, context) =>
        {
            foreach (var error in MetricAlarmValidation.Validate(request))
                context.AddFailure(error.Field, error.Message);
        });
    }
}
