using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Domain.Enums;
using FluentValidation;

namespace CardiTrack.API.Validators;

/// <summary>
/// The business rules the reports endpoint shipped without. Until now malformed values failed
/// model binding and nothing else was checked, so a request could ask for every member the caller
/// could reach across a decade — a query and a render with no ceiling on either.
/// </summary>
public class GenerateReportValidator : AbstractValidator<GenerateReportRequest>
{
    /// <summary>
    /// Documented in the reports API as the cap the endpoint was always meant to have. Five is
    /// more than a family dashboard shows, so it bounds the work without bounding real use.
    /// </summary>
    public const int MaxCardiMembers = 5;

    /// <summary>
    /// A year. Long enough for "everything since we started monitoring" — the app has not been
    /// collecting for longer — and short enough that one export stays a document rather than an
    /// archive dump.
    /// </summary>
    public const int MaxRangeDays = 365;

    public GenerateReportValidator()
    {
        RuleFor(x => x.CardiMemberIds)
            .NotEmpty().WithMessage("Choose at least one person to export data for")
            .Must(ids => ids.Count <= MaxCardiMembers)
                .WithMessage($"You can export up to {MaxCardiMembers} people at a time")
            .Must(ids => ids.Distinct().Count() == ids.Count)
                .WithMessage("Each person can only be included once");

        RuleFor(x => x.DateRangeTo)
            .GreaterThanOrEqualTo(x => x.DateRangeFrom)
                .WithMessage("The end date must be on or after the start date");

        RuleFor(x => x)
            .Must(x => x.DateRangeTo.DayNumber - x.DateRangeFrom.DayNumber < MaxRangeDays)
                .WithMessage($"Choose a date range of up to {MaxRangeDays} days")
            .When(x => x.DateRangeTo >= x.DateRangeFrom);

        // Only the three MVP 1 formats render. HL7 v2 is a defined enum member for MVP 2, so
        // without this it would be accepted here and fail later as a generation error the
        // caregiver could do nothing about.
        RuleFor(x => x.Format)
            .Must(f => f is ReportFormat.Pdf or ReportFormat.Csv or ReportFormat.FhirR4)
                .WithMessage("Choose PDF, CSV or FHIR R4");

        // Every section off would produce a file with a header and nothing under it.
        RuleFor(x => x)
            .Must(x => x.IncludeMetrics || x.IncludeAlerts || x.IncludeDevices)
                .WithMessage("Choose at least one kind of data to include");
    }
}
