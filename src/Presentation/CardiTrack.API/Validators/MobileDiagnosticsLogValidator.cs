using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Shared.Http;
using FluentValidation;

namespace CardiTrack.API.Validators;

/// <summary>
/// The ceilings in <see cref="MobileDiagnosticsContract"/>, enforced. The app truncates to the
/// same numbers before it queues, so a well-behaved build never trips these; they exist for the
/// build that is not ours, and to keep one crash from arriving as a megabyte of stack.
/// </summary>
public class MobileDiagnosticsLogValidator : AbstractValidator<MobileDiagnosticsLogRequest>
{
    private static readonly string[] Levels = ["Warning", "Error", "Fatal"];

    public MobileDiagnosticsLogValidator()
    {
        RuleFor(x => x.Entries)
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("Send at least one log entry")
            .Must(entries => entries.Count <= MobileDiagnosticsContract.MaxEntriesPerBatch)
                .WithMessage($"Send at most {MobileDiagnosticsContract.MaxEntriesPerBatch} entries per batch");

        foreach (var field in new[]
        {
            (Func<MobileDiagnosticsLogRequest, string?>)(x => x.Platform),
            x => x.AppVersion,
            x => x.Device,
            x => x.Manufacturer,
            x => x.Model,
            x => x.OsVersion,
            x => x.OsDescription,
            x => x.Architecture,
            x => x.Runtime,
            x => x.Locale,
            x => x.TimeZone,
            x => x.AppAssemblyVersion,
            x => x.ModuleVersionId,
            x => x.InstallId,
        })
        {
            RuleFor(x => field(x)).MaximumLength(MobileDiagnosticsContract.MaxFieldLength);
        }

        RuleForEach(x => x.Entries).ChildRules(entry =>
        {
            entry.RuleFor(e => e.Level)
                .Must(level => Levels.Contains(level, StringComparer.OrdinalIgnoreCase))
                .WithMessage("Level must be Warning, Error or Fatal");
            entry.RuleFor(e => e.Message)
                .NotEmpty()
                .MaximumLength(MobileDiagnosticsContract.MaxMessageLength);
            entry.RuleFor(e => e.Exception).MaximumLength(MobileDiagnosticsContract.MaxExceptionLength);
            entry.RuleFor(e => e.ExceptionType).MaximumLength(MobileDiagnosticsContract.MaxFrameFieldLength);
            entry.RuleFor(e => e.ExceptionMessage).MaximumLength(MobileDiagnosticsContract.MaxMessageLength);
            entry.RuleFor(e => e.Source).MaximumLength(MobileDiagnosticsContract.MaxFieldLength);
            entry.RuleFor(e => e.Screen).MaximumLength(MobileDiagnosticsContract.MaxFieldLength);
            entry.RuleFor(e => e.NetworkAccess).MaximumLength(MobileDiagnosticsContract.MaxFieldLength);
            entry.RuleFor(e => e.ThreadName).MaximumLength(MobileDiagnosticsContract.MaxFieldLength);
            entry.RuleFor(e => e.RecentLog).MaximumLength(MobileDiagnosticsContract.MaxRecentLogLength);

            entry.RuleFor(e => e.Frames)
                .Must(frames => frames.Count <= MobileDiagnosticsContract.MaxFrames)
                .WithMessage($"Send at most {MobileDiagnosticsContract.MaxFrames} frames per entry");
            entry.RuleForEach(e => e.Frames).ChildRules(frame =>
            {
                frame.RuleFor(f => f.Type).MaximumLength(MobileDiagnosticsContract.MaxFrameFieldLength);
                frame.RuleFor(f => f.Method).MaximumLength(MobileDiagnosticsContract.MaxFrameFieldLength);
                frame.RuleFor(f => f.Assembly).MaximumLength(MobileDiagnosticsContract.MaxFrameFieldLength);
                frame.RuleFor(f => f.File).MaximumLength(MobileDiagnosticsContract.MaxFrameFieldLength);
                frame.RuleFor(f => f.NativeIp).MaximumLength(MobileDiagnosticsContract.MaxFieldLength);
                frame.RuleFor(f => f.NativeImageBase).MaximumLength(MobileDiagnosticsContract.MaxFieldLength);
            });
        });
    }
}
