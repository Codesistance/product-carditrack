using CardiTrack.Application.Reports;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Interfaces.Services;

/// <summary>
/// Renders a gathered <see cref="ReportDataSet"/> into one export format. One implementation per
/// <see cref="ReportFormat"/>; <c>ReportGenerationService</c> resolves the set and dispatches on
/// <see cref="Format"/>, so adding HL7 v2 in MVP 2 means adding a renderer, not editing a switch.
/// </summary>
public interface IReportRenderer
{
    /// <summary>The format this renderer produces. Unique across registered renderers.</summary>
    ReportFormat Format { get; }

    /// <summary>
    /// Renders the export.
    /// </summary>
    /// <param name="narrative">
    /// The AI-written caregiver summary with real names already restored, or null when the
    /// renderer's format has no place for prose. Never the pseudonymised text: the swap back
    /// happens before rendering, so the model's copy of the data and the caregiver's copy differ
    /// exactly where they must.
    /// </param>
    Task<RenderedReport> RenderAsync(
        ReportDataSet data,
        ReportSections sections,
        string? narrative,
        CancellationToken ct = default);
}
