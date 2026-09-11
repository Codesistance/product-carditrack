using CardiTrack.API.Infrastructure.Auditing;
using CardiTrack.API.Infrastructure.UserContext;
using CardiTrack.API.Validators;
using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Interfaces.Services;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CardiTrack.API.Controllers;

[Authorize]
[AuditHealthDataAccess("Report")]
[Route("api/v1/reports")]
public class ReportsController : BaseApiController
{
    private readonly IReportGenerationService _reportService;
    private readonly IExportConsentService _consent;
    private readonly IValidator<GenerateReportRequest> _generateValidator;
    private readonly IValidator<RecordExportConsentRequest> _consentValidator;
    private readonly ReuseExportConsentValidator _reuseValidator;

    public ReportsController(
        IUserContext userContext,
        ILogger<ReportsController> logger,
        IReportGenerationService reportService,
        IExportConsentService consent,
        IValidator<GenerateReportRequest> generateValidator,
        IValidator<RecordExportConsentRequest> consentValidator,
        ReuseExportConsentValidator reuseValidator)
        : base(userContext, logger)
    {
        _reportService = reportService;
        _consent = consent;
        _generateValidator = generateValidator;
        _consentValidator = consentValidator;
        _reuseValidator = reuseValidator;
    }

    /// <summary>
    /// Records that the caregiver accepted responsibility for this export and
    /// proved it (password or biometrics). Returns a short-lived token the
    /// generate call must present.
    /// </summary>
    [HttpPost("consent")]
    [ProducesResponseType(typeof(ApiResponse<ExportConsentResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<ExportConsentResponse>>> RecordConsent(
        [FromBody] RecordExportConsentRequest request, CancellationToken ct)
    {
        if (NotSignedIn(out var signInError))
            return signInError;

        var validation = await _consentValidator.ValidateAsync(request, ct);
        if (!validation.IsValid)
            return ValidationFailed(validation);

        try
        {
            var recorded = await _consent.RecordAsync(UserContext.UserId, request, ct);
            return Success(recorded, "Confirmed — we can prepare the export now.");
        }
        catch (KeyNotFoundException ex)
        {
            return Error(ex.Message, StatusCodes.Status404NotFound);
        }
    }

    /// <summary>
    /// Mints a two-minute token from an in-force standing grant for this snapshot.
    /// The client must tell the caregiver the confirmation is being reused.
    /// </summary>
    [HttpPost("consent/reuse")]
    [AuditHealthDataAccess("ReuseExportConsent")]
    [ProducesResponseType(typeof(ApiResponse<ExportConsentResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<ExportConsentResponse>>> ReuseConsent(
        [FromBody] GenerateReportRequest request, CancellationToken ct)
    {
        if (NotSignedIn(out var signInError))
            return signInError;

        var validation = await _reuseValidator.ValidateAsync(request, ct);
        if (!validation.IsValid)
            return ValidationFailed(validation);

        try
        {
            var reused = await _consent.ReuseAsync(UserContext.UserId, request, ct);
            return Success(reused, reused.ReuseNotice ?? "We're using your earlier confirmation.");
        }
        catch (KeyNotFoundException ex)
        {
            return Error(ex.Message, StatusCodes.Status404NotFound);
        }
    }

    /// <summary>Every confirmation this caregiver has given, newest first.</summary>
    [HttpGet("consents")]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<ExportConsentHistoryItem>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<ExportConsentHistoryItem>>>> ListConsents(
        CancellationToken ct)
    {
        if (NotSignedIn(out var signInError))
            return signInError;

        return Success(await _consent.ListAsync(UserContext.UserId, ct));
    }

    /// <summary>Stops a standing grant. Later exports must confirm again.</summary>
    [HttpDelete("consents/{consentId:guid}")]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<object>>> RevokeConsent(Guid consentId, CancellationToken ct)
    {
        if (NotSignedIn(out var signInError))
            return signInError;

        try
        {
            await _consent.RevokeAsync(UserContext.UserId, consentId, ct);
            return Success("That confirmation is no longer in force.");
        }
        catch (KeyNotFoundException ex)
        {
            return Error(ex.Message, StatusCodes.Status404NotFound);
        }
    }

    /// <summary>Enqueue a report for generation. Returns 202 immediately with a report ID to poll.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(ApiResponse<ReportQueuedResponse>), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<ReportQueuedResponse>>> Generate(
        [FromBody] GenerateReportRequest request, CancellationToken ct)
    {
        if (NotSignedIn(out var signInError))
            return signInError;

        var validation = await _generateValidator.ValidateAsync(request, ct);
        if (!validation.IsValid)
            return ValidationFailed(validation);

        try
        {
            var result = await _reportService.GenerateAsync(UserContext.UserId, request);
            return Queued(result, "We're preparing your report — it'll be ready shortly!");
        }
        catch (KeyNotFoundException ex)
        {
            return Error(ex.Message, StatusCodes.Status404NotFound);
        }
    }

    /// <summary>Get current status of a queued or completed report.</summary>
    [HttpGet("{reportId}")]
    [ProducesResponseType(typeof(ApiResponse<ReportStatusResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<ReportStatusResponse>>> GetStatus(string reportId)
    {
        if (NotSignedIn(out var signInError))
            return signInError;

        // Ownership is what protects a report: the service answers only for the caller's own rows.
        var status = await _reportService.GetStatusAsync(UserContext.UserId, reportId);
        if (status is null)
            return Error("We couldn't find that report — it may have expired. Try generating a new one.", StatusCodes.Status404NotFound);

        return Success(status);
    }

    /// <summary>Download a completed report.</summary>
    [HttpGet("{reportId}/download")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Download(string reportId)
    {
        if (NotSignedIn(out var signInError))
            return signInError;

        try
        {
            var (content, contentType, fileName) = await _reportService.DownloadAsync(UserContext.UserId, reportId);

            // The bytes are proxied rather than redirected to a signed bucket URL: a signed URL
            // would be a bearer capability to a full health record, outside this authorization
            // check and invisible to the [AuditHealthDataAccess] row this request writes.
            return File(content, contentType, fileName);
        }
        catch (KeyNotFoundException ex)
        {
            return Error(ex.Message, StatusCodes.Status404NotFound);
        }
        catch (InvalidOperationException ex)
        {
            return Error(ex.Message, StatusCodes.Status409Conflict);
        }
    }

    private bool NotSignedIn(out ActionResult error)
    {
        if (!UserContext.IsAuthenticated || UserContext.UserId == Guid.Empty)
        {
            error = Error("We couldn't find your account — please sign in again.", StatusCodes.Status403Forbidden);
            return true;
        }

        error = null!;
        return false;
    }
}
