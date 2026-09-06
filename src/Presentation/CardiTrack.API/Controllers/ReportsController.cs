using CardiTrack.API.Infrastructure.Auditing;
using CardiTrack.API.Infrastructure.UserContext;
using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Domain.Enums;
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
    private readonly IEntitlementService _entitlements;
    private readonly IValidator<GenerateReportRequest> _generateValidator;

    public ReportsController(
        IUserContext userContext,
        ILogger<ReportsController> logger,
        IReportGenerationService reportService,
        IEntitlementService entitlements,
        IValidator<GenerateReportRequest> generateValidator)
        : base(userContext, logger)
    {
        _reportService = reportService;
        _entitlements = entitlements;
        _generateValidator = generateValidator;
    }

    /// <summary>Enqueue a report for generation. Returns 202 immediately with a report ID to poll.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(ApiResponse<ReportQueuedResponse>), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status402PaymentRequired)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<ReportQueuedResponse>>> Generate(
        [FromBody] GenerateReportRequest request, CancellationToken ct)
    {
        if (NotSignedIn(out var signInError))
            return signInError;

        // Plan before shape: a Basic caregiver should be told their plan doesn't include export,
        // not handed a list of field errors for a request that was never going to run.
        try
        {
            await _entitlements.RequireAsync(
                UserContext.OrganizationId, PlanFeature.HealthDataExport, ct);
        }
        catch (FeatureNotEntitledException ex)
        {
            // 402, not 403: this is "your plan doesn't cover this", which is answerable by
            // upgrading — distinct from the 403 that means we don't know who you are.
            return Error(ex.Message, StatusCodes.Status402PaymentRequired);
        }

        var validation = await _generateValidator.ValidateAsync(request, ct);
        if (!validation.IsValid)
            return ValidationFailed(validation);

        try
        {
            var result = await _reportService.GenerateAsync(UserContext.UserId, request);
            return Accepted(Success(result, "We're preparing your report — it'll be ready shortly!").Value);
        }
        catch (KeyNotFoundException ex)
        {
            return Error(ex.Message, StatusCodes.Status404NotFound);
        }
    }

    /// <summary>
    /// Whether the caller's plan includes export, so the client can offer the upgrade instead of
    /// a form that would be refused. A convenience, never the gate — <c>POST</c> checks for itself.
    /// </summary>
    [HttpGet("availability")]
    [ProducesResponseType(typeof(ApiResponse<ReportAvailabilityResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ApiResponse<ReportAvailabilityResponse>>> Availability(CancellationToken ct)
    {
        if (NotSignedIn(out var signInError))
            return signInError;

        try
        {
            await _entitlements.RequireAsync(
                UserContext.OrganizationId, PlanFeature.HealthDataExport, ct);

            return Success(new ReportAvailabilityResponse
            {
                Available = true,
                RequiredTier = SubscriptionTier.Complete
            });
        }
        catch (FeatureNotEntitledException ex)
        {
            // 200 with Available=false, not 402: the client asked whether it may offer export,
            // and "no" is a successful answer to that question.
            return Success(new ReportAvailabilityResponse
            {
                Available = false,
                Message = ex.Message,
                RequiredTier = ex.RequiredTier
            });
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

        // No entitlement check on the read paths: a plan that lapses after generation must not
        // strip a caregiver of a health record they already asked for and may already be relying
        // on. Ownership is what protects it, and ownership does not expire with billing.
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
