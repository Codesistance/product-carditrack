using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.Reports;
using CardiTrack.Domain.Enums;
using FluentValidation;

namespace CardiTrack.API.Validators;

/// <summary>
/// Same ceilings as <see cref="GenerateReportValidator"/> — a consent for an
/// illegal generate must not mint a token the generate call would then refuse.
/// </summary>
public class RecordExportConsentValidator : AbstractValidator<RecordExportConsentRequest>
{
    public RecordExportConsentValidator()
    {
        RuleFor(x => x.CardiMemberIds)
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("Choose at least one person to export data for")
            .Must(ids => ids.Count <= GenerateReportValidator.MaxCardiMembers)
                .WithMessage($"You can export up to {GenerateReportValidator.MaxCardiMembers} people at a time")
            .Must(ids => ids.Distinct().Count() == ids.Count)
                .WithMessage("Each person can only be included once");

        RuleFor(x => x.DateRangeTo)
            .GreaterThanOrEqualTo(x => x.DateRangeFrom)
                .WithMessage("The end date must be on or after the start date");

        RuleFor(x => x)
            .Must(x => x.DateRangeTo.DayNumber - x.DateRangeFrom.DayNumber < GenerateReportValidator.MaxRangeDays)
                .WithMessage($"Choose a date range of up to {GenerateReportValidator.MaxRangeDays} days")
            .When(x => x.DateRangeTo >= x.DateRangeFrom);

        RuleFor(x => x.Format)
            .Must(f => f is ReportFormat.Pdf or ReportFormat.Csv or ReportFormat.FhirR4)
                .WithMessage("Choose PDF, CSV or FHIR R4");

        RuleFor(x => x)
            .Must(x => x.IncludeMetrics || x.IncludeAlerts || x.IncludeDevices
                       || x.IncludeJournals || x.IncludeNotices)
                .WithMessage("Choose at least one kind of data to include");

        RuleFor(x => x)
            .Must(x => x.IncludeMetrics || x.IncludeDevices)
                .WithMessage("FHIR R4 exports carry readings and devices — tick one of those too, "
                    + "or choose PDF or CSV to export journals, alerts or notices")
            .When(x => x.Format == ReportFormat.FhirR4);

        RuleFor(x => x)
            .Must(x => ExportJournalRules.ScopeMatchesJournalsFlag(
                x.IncludeJournals, x.JournalAudience, x.JournalEntryDate))
                .WithMessage(ExportJournalRules.ScopeNeedsJournals);

        RuleFor(x => x.JournalAudience)
            .Must(ExportJournalRules.AudienceIsAllowed)
                .WithMessage(ExportJournalRules.FinishedBooksOnly);

        RuleFor(x => x)
            .Must(x => ReportJournalScope.DayIsInRange(
                x.JournalEntryDate, x.DateRangeFrom, x.DateRangeTo))
                .WithMessage(ExportJournalRules.DayInRange)
            .When(x => x.DateRangeTo >= x.DateRangeFrom);

        RuleFor(x => x.AcceptedResponsibility)
            .Equal(true)
            .WithMessage("Confirm you accept responsibility before exporting");

        RuleFor(x => x.Method)
            .IsInEnum()
            .WithMessage("Confirm with your password or this device's fingerprint or face unlock");
    }
}
