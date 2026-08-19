using CardiTrack.API.Infrastructure.Auditing;
using CardiTrack.API.Infrastructure.UserContext;
using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Interfaces.Services;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CardiTrack.API.Controllers;

/// <summary>
/// CardiMember detail, editing, monitoring pause and removal — the M1-13/M1-14 screens.
/// Creation and listing still live on <see cref="OnboardingController"/>; see
/// docs/execution/backend/api/cardimembers.md.
/// </summary>
/// <remarks>
/// Every action reports a denied CardiMember as 404, never 403: a 403 would confirm the
/// member exists, which is itself a disclosure. See <see cref="ICardiMemberAccessService"/>.
/// </remarks>
[Authorize]
[AuditHealthDataAccess("AccessCardiMember")]
[Route("api/v1")]
public class CardiMembersController : BaseApiController
{
    private readonly ICardiMemberService _cardiMembers;
    private readonly IAlertPreferenceService _alertPreferences;
    private readonly IJournalSettingsService _journalSettings;
    private readonly IValidator<UpdateCardiMemberRequest> _updateValidator;
    private readonly IValidator<PauseMonitoringRequest> _pauseValidator;

    public CardiMembersController(
        IUserContext userContext,
        ILogger<CardiMembersController> logger,
        ICardiMemberService cardiMembers,
        IAlertPreferenceService alertPreferences,
        IJournalSettingsService journalSettings,
        IValidator<UpdateCardiMemberRequest> updateValidator,
        IValidator<PauseMonitoringRequest> pauseValidator)
        : base(userContext, logger)
    {
        _cardiMembers = cardiMembers;
        _alertPreferences = alertPreferences;
        _journalSettings = journalSettings;
        _updateValidator = updateValidator;
        _pauseValidator = pauseValidator;
    }

    /// <summary>Full profile for one CardiMember (M1-13).</summary>
    [HttpGet("cardimembers/{cardiMemberId:guid}")]
    [ProducesResponseType(typeof(ApiResponse<CardiMemberDetailResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<CardiMemberDetailResponse>>> GetDetail(
        Guid cardiMemberId, CancellationToken ct)
    {
        if (NotSignedIn(out var error))
            return error;

        try
        {
            return Success(await _cardiMembers.GetDetailAsync(UserContext.UserId, cardiMemberId, ct));
        }
        catch (KeyNotFoundException ex)
        {
            return Error(ex.Message, StatusCodes.Status404NotFound);
        }
    }

    /// <summary>Saves the edit form (M1-14).</summary>
    [HttpPut("cardimembers/{cardiMemberId:guid}")]
    [ProducesResponseType(typeof(ApiResponse<CardiMemberDetailResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<CardiMemberDetailResponse>>> Update(
        Guid cardiMemberId, [FromBody] UpdateCardiMemberRequest request, CancellationToken ct)
    {
        if (NotSignedIn(out var error))
            return error;

        var validation = await _updateValidator.ValidateAsync(request, ct);
        if (!validation.IsValid)
            return ValidationFailed(validation);

        try
        {
            var updated = await _cardiMembers.UpdateAsync(UserContext.UserId, cardiMemberId, request, ct);
            return Success(updated, $"{updated.Name}'s details are saved.");
        }
        catch (KeyNotFoundException ex)
        {
            return Error(ex.Message, StatusCodes.Status404NotFound);
        }
    }

    /// <summary>
    /// Removes a CardiMember (M1-13 "Remove CardiMember"). Soft delete — health history is
    /// retained for the retention window, but monitoring stops immediately.
    /// </summary>
    [HttpDelete("cardimembers/{cardiMemberId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult> Remove(Guid cardiMemberId, CancellationToken ct)
    {
        if (NotSignedIn(out var error))
            return error;

        try
        {
            await _cardiMembers.RemoveAsync(UserContext.UserId, cardiMemberId, ct);
            Logger.LogInformation(
                "CardiMember {CardiMemberId} removed by user {UserId}", cardiMemberId, UserContext.UserId);
            return NoContent();
        }
        catch (KeyNotFoundException ex)
        {
            return Error(ex.Message, StatusCodes.Status404NotFound);
        }
    }

    /// <summary>Pauses monitoring for a bounded window (M1-13).</summary>
    [HttpPost("cardimembers/{cardiMemberId:guid}/pause")]
    [ProducesResponseType(typeof(ApiResponse<MonitoringPauseResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<MonitoringPauseResponse>>> Pause(
        Guid cardiMemberId, [FromBody] PauseMonitoringRequest request, CancellationToken ct)
    {
        if (NotSignedIn(out var error))
            return error;

        var validation = await _pauseValidator.ValidateAsync(request, ct);
        if (!validation.IsValid)
            return ValidationFailed(validation);

        try
        {
            var state = await _cardiMembers.PauseMonitoringAsync(UserContext.UserId, cardiMemberId, request, ct);
            Logger.LogInformation(
                "Monitoring paused for CardiMember {CardiMemberId} until {Until} by user {UserId}",
                cardiMemberId, state.MonitoringPausedUntil, UserContext.UserId);
            return Success(state, "Monitoring is paused — we'll pick things back up automatically.");
        }
        catch (KeyNotFoundException ex)
        {
            return Error(ex.Message, StatusCodes.Status404NotFound);
        }
    }

    /// <summary>Resumes monitoring ahead of the scheduled time (M1-13).</summary>
    [HttpDelete("cardimembers/{cardiMemberId:guid}/pause")]
    [ProducesResponseType(typeof(ApiResponse<MonitoringPauseResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<MonitoringPauseResponse>>> Resume(
        Guid cardiMemberId, CancellationToken ct)
    {
        if (NotSignedIn(out var error))
            return error;

        try
        {
            var state = await _cardiMembers.ResumeMonitoringAsync(UserContext.UserId, cardiMemberId, ct);
            Logger.LogInformation(
                "Monitoring resumed for CardiMember {CardiMemberId} by user {UserId}",
                cardiMemberId, UserContext.UserId);
            return Success(state, "Monitoring is back on.");
        }
        catch (KeyNotFoundException ex)
        {
            return Error(ex.Message, StatusCodes.Status404NotFound);
        }
    }

    /// <summary>
    /// Per-CardiMember alert-rule catalogue with effective on/off state (M1-13). Missing
    /// preference rows mean every rule is on.
    /// </summary>
    [HttpGet("cardimembers/{cardiMemberId:guid}/alert-preferences")]
    [ProducesResponseType(typeof(ApiResponse<AlertPreferencesResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<AlertPreferencesResponse>>> GetAlertPreferences(
        Guid cardiMemberId, CancellationToken ct)
    {
        if (NotSignedIn(out var error))
            return error;

        try
        {
            return Success(await _alertPreferences.GetAsync(UserContext.UserId, cardiMemberId, ct));
        }
        catch (KeyNotFoundException ex)
        {
            return Error(ex.Message, StatusCodes.Status404NotFound);
        }
    }

    /// <summary>
    /// Instant toggle for one alert rule on a CardiMember. Off means the producer skips that
    /// rule entirely. Primary caregiver only.
    /// </summary>
    [HttpPatch("cardimembers/{cardiMemberId:guid}/alert-preferences/rules/{ruleId}")]
    [ProducesResponseType(typeof(ApiResponse<AlertRuleSettingResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<AlertRuleSettingResponse>>> SetAlertRuleEnabled(
        Guid cardiMemberId,
        string ruleId,
        [FromBody] SetAlertRuleEnabledRequest request,
        CancellationToken ct)
    {
        if (NotSignedIn(out var error))
            return error;

        if (request is null)
            return Error("Request body is required.", StatusCodes.Status400BadRequest);

        if (request.Enabled is not { } enabled)
            return Error("enabled is required.", StatusCodes.Status400BadRequest);

        try
        {
            var updated = await _alertPreferences.SetRuleEnabledAsync(
                UserContext.UserId, cardiMemberId, ruleId, enabled, ct);
            return Success(updated, updated.Enabled ? "Alert rule is on." : "Alert rule is off.");
        }
        catch (KeyNotFoundException ex)
        {
            return Error(ex.Message, StatusCodes.Status404NotFound);
        }
        catch (ArgumentException ex)
        {
            return Error(ex.Message, StatusCodes.Status400BadRequest);
        }
        catch (InvalidOperationException ex)
        {
            return Error(ex.Message, StatusCodes.Status400BadRequest);
        }
    }

    /// <summary>
    /// When this CardiMember's CardiJournal books are written, in their own local time, plus the
    /// window and step a client must keep its picker inside.
    /// </summary>
    [HttpGet("cardimembers/{cardiMemberId:guid}/journal-settings")]
    [ProducesResponseType(typeof(ApiResponse<JournalSettingsResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<JournalSettingsResponse>>> GetJournalSettings(
        Guid cardiMemberId, CancellationToken ct)
    {
        if (NotSignedIn(out var error))
            return error;

        try
        {
            return Success(await _journalSettings.GetAsync(UserContext.UserId, cardiMemberId, ct));
        }
        catch (KeyNotFoundException ex)
        {
            return Error(ex.Message, StatusCodes.Status404NotFound);
        }
    }

    /// <summary>
    /// Moves when this CardiMember's books are written. Primary caregiver only — a book is written
    /// once for the member, so the time is the member's, not each reader's. A null field restores
    /// that book's default.
    /// </summary>
    [HttpPut("cardimembers/{cardiMemberId:guid}/journal-settings")]
    [ProducesResponseType(typeof(ApiResponse<JournalSettingsResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<JournalSettingsResponse>>> UpdateJournalSettings(
        Guid cardiMemberId,
        [FromBody] UpdateJournalSettingsRequest request,
        CancellationToken ct)
    {
        if (NotSignedIn(out var error))
            return error;

        if (request is null)
            return Error("Request body is required.", StatusCodes.Status400BadRequest);

        try
        {
            var updated = await _journalSettings.UpdateAsync(
                UserContext.UserId, cardiMemberId, request, ct);
            return Success(updated, "Saved when their journal is written.");
        }
        catch (KeyNotFoundException ex)
        {
            return Error(ex.Message, StatusCodes.Status404NotFound);
        }
        catch (ArgumentException ex)
        {
            return Error(ex.Message, StatusCodes.Status400BadRequest);
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
