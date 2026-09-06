using CardiTrack.API.Infrastructure.Auditing;
using CardiTrack.API.Infrastructure.UserContext;
using CardiTrack.Application.DTOs.Common;
using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Interfaces.Services;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CardiTrack.API.Controllers;

/// <summary>
/// User-defined metric alarms — the caregiver's own thresholds, set once for the account and
/// tuned per CardiMember.
/// <para>
/// Reads need view access to the member; writes need primary-caregiver authority. A denial returns
/// <b>404, not 403</b>, matching the rest of this API: a caller must not be able to learn that an
/// alarm exists by being refused it.
/// </para>
/// </summary>
[Authorize]
// Class level, matching CardiMembersController, rather than per action. An opt-in that has to be
// remembered on every new route develops a hole the moment somebody adds one, and the failure is
// invisible — the trail just quietly stops covering an endpoint. The cost is a few low-value rows
// for the catalogue route, which carries no member data; on a six-year HIPAA trail that is the
// cheaper mistake. The method-level attribute on GetMemberAlarms still wins for that action, since
// the middleware reads the last matching metadata entry.
[AuditHealthDataAccess("AccessMetricAlarm")]
[Route("api/v1")]
public class MetricAlarmsController : BaseApiController
{
    private readonly IMetricAlarmService _alarms;
    private readonly IValidator<SaveMetricAlarmRequest> _validator;

    public MetricAlarmsController(
        IUserContext userContext,
        ILogger<MetricAlarmsController> logger,
        IMetricAlarmService alarms,
        IValidator<SaveMetricAlarmRequest> validator)
        : base(userContext, logger)
    {
        _alarms = alarms;
        _validator = validator;
    }

    /// <summary>
    /// What an alarm may legally be built from — metric by metric, the statistics that mean
    /// anything on it, the periods it can be watched over, and the band its threshold must sit in.
    /// The builder reads this so an illegal combination is unreachable rather than merely refused.
    /// </summary>
    [HttpGet("alarms/catalogue")]
    [ProducesResponseType(typeof(ApiResponse<AlarmCatalogueResponse>), StatusCodes.Status200OK)]
    public ActionResult<ApiResponse<AlarmCatalogueResponse>> GetCatalogue()
    {
        if (NotSignedIn(out var error))
            return error;

        return Success(_alarms.GetCatalogue());
    }

    /// <summary>The account-level defaults every CardiMember inherits.</summary>
    [HttpGet("alarms")]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<MetricAlarmResponse>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public Task<ActionResult<ApiResponse<IReadOnlyList<MetricAlarmResponse>>>> GetAccountAlarms(
        CancellationToken ct)
        => Guarded(() => _alarms.GetAccountAlarmsAsync(UserContext.UserId, ct));

    /// <summary>Creates an account-level default.</summary>
    [HttpPost("alarms")]
    [ProducesResponseType(typeof(ApiResponse<MetricAlarmResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<MetricAlarmResponse>>> CreateAccountAlarm(
        [FromBody] SaveMetricAlarmRequest request, CancellationToken ct)
    {
        if (NotSignedIn(out var error))
            return error;

        if (await Invalid(request, ct) is { } invalid)
            return invalid;

        return await GuardedCreate(() => _alarms.CreateAccountAlarmAsync(UserContext.UserId, request, ct));
    }

    /// <summary>Replaces an account-level default.</summary>
    [HttpPut("alarms/{alarmId:guid}")]
    [ProducesResponseType(typeof(ApiResponse<MetricAlarmResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<MetricAlarmResponse>>> UpdateAccountAlarm(
        Guid alarmId, [FromBody] SaveMetricAlarmRequest request, CancellationToken ct)
    {
        if (NotSignedIn(out var error))
            return error;

        if (await Invalid(request, ct) is { } invalid)
            return invalid;

        return await Guarded(() => _alarms.UpdateAccountAlarmAsync(UserContext.UserId, alarmId, request, ct));
    }

    /// <summary>
    /// Removes an account-level default. Members who had tuned it keep their own copy — see
    /// <c>MetricAlarmResolution</c>: a caregiver's tuning for one person is an intention about that
    /// person, and deleting the shared default is not a retraction of it.
    /// </summary>
    [HttpDelete("alarms/{alarmId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult> DeleteAccountAlarm(Guid alarmId, CancellationToken ct)
    {
        if (NotSignedIn(out var error))
            return error;

        try
        {
            await _alarms.DeleteAccountAlarmAsync(UserContext.UserId, alarmId, ct);
            return NoContent();
        }
        catch (KeyNotFoundException ex)
        {
            return Error(ex.Message, StatusCodes.Status404NotFound);
        }
    }

    /// <summary>
    /// The alarms that actually apply to one CardiMember — account defaults folded together with
    /// this member's overrides and additions, each saying where it came from and where it stands.
    /// </summary>
    [HttpGet("cardimembers/{cardiMemberId:guid}/alarms")]
    [AuditHealthDataAccess("ViewMetricAlarms")]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<MetricAlarmResponse>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public Task<ActionResult<ApiResponse<IReadOnlyList<MetricAlarmResponse>>>> GetMemberAlarms(
        Guid cardiMemberId, CancellationToken ct)
        => Guarded(() => _alarms.GetMemberAlarmsAsync(UserContext.UserId, cardiMemberId, ct));

    /// <summary>Adds an alarm for this CardiMember alone. Primary caregiver only.</summary>
    [HttpPost("cardimembers/{cardiMemberId:guid}/alarms")]
    [ProducesResponseType(typeof(ApiResponse<MetricAlarmResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<MetricAlarmResponse>>> CreateMemberAlarm(
        Guid cardiMemberId, [FromBody] SaveMetricAlarmRequest request, CancellationToken ct)
    {
        if (NotSignedIn(out var error))
            return error;

        if (await Invalid(request, ct) is { } invalid)
            return invalid;

        return await GuardedCreate(
            () => _alarms.CreateMemberAlarmAsync(UserContext.UserId, cardiMemberId, request, ct));
    }

    /// <summary>
    /// Sets what applies to this CardiMember for one alarm. Given an account default's id this
    /// writes the member's override of it; given a member alarm's own id it edits that row. Saving
    /// with <c>isEnabled</c> false is how a member opts out of an inherited alarm.
    /// </summary>
    [HttpPut("cardimembers/{cardiMemberId:guid}/alarms/{alarmId:guid}")]
    [ProducesResponseType(typeof(ApiResponse<MetricAlarmResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<MetricAlarmResponse>>> SaveMemberAlarm(
        Guid cardiMemberId, Guid alarmId, [FromBody] SaveMetricAlarmRequest request, CancellationToken ct)
    {
        if (NotSignedIn(out var error))
            return error;

        if (await Invalid(request, ct) is { } invalid)
            return invalid;

        return await Guarded(
            () => _alarms.SaveMemberOverrideAsync(UserContext.UserId, cardiMemberId, alarmId, request, ct));
    }

    /// <summary>
    /// Removes what this CardiMember has of their own for an alarm — reverting an override to the
    /// account default, or deleting an alarm that was only ever theirs.
    /// </summary>
    [HttpDelete("cardimembers/{cardiMemberId:guid}/alarms/{alarmId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult> DeleteMemberAlarm(Guid cardiMemberId, Guid alarmId, CancellationToken ct)
    {
        if (NotSignedIn(out var error))
            return error;

        try
        {
            await _alarms.DeleteMemberAlarmAsync(UserContext.UserId, cardiMemberId, alarmId, ct);
            return NoContent();
        }
        catch (KeyNotFoundException ex)
        {
            return Error(ex.Message, StatusCodes.Status404NotFound);
        }
    }

    // ── plumbing ─────────────────────────────────────────────────────────────────────────

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

    private async Task<ActionResult<ApiResponse<MetricAlarmResponse>>?> Invalid(
        SaveMetricAlarmRequest? request, CancellationToken ct)
    {
        if (request is null)
            return Error("Request body is required.", StatusCodes.Status400BadRequest);

        var result = await _validator.ValidateAsync(request, ct);
        if (result.IsValid)
            return null;

        return ValidationFailed(result);
    }

    private async Task<ActionResult<ApiResponse<T>>> Guarded<T>(Func<Task<T>> work)
    {
        if (NotSignedIn(out var error))
            return error!;

        try
        {
            return Success(await work());
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
        catch (DbUpdateException)
        {
            return Error(ConcurrentSaveMessage, StatusCodes.Status409Conflict);
        }
    }

    private async Task<ActionResult<ApiResponse<MetricAlarmResponse>>> GuardedCreate(
        Func<Task<MetricAlarmResponse>> work)
    {
        try
        {
            return Created(await work(), "Alarm created.");
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
        catch (DbUpdateException)
        {
            return Error(ConcurrentSaveMessage, StatusCodes.Status409Conflict);
        }
    }

    // Two devices writing the same member's first override at once both read "no override yet",
    // and the unique index on (member, default) refuses the loser. The winner's row stands, so the
    // loser is told to look again rather than shown a server error.
    private const string ConcurrentSaveMessage =
        "This alarm was just changed from another device. Refresh and try again.";
}
