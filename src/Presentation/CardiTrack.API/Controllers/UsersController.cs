using CardiTrack.API.Infrastructure.UserContext;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Interfaces.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CardiTrack.API.Controllers;

/// <summary>
/// The signed-in caregiver's own account settings.
/// </summary>
/// <remarks>
/// The time zone — added alongside the notification engine because the <c>TIMEZONE_DEFAULT</c>
/// nudge asks the user to set it, and a prompt whose action leads nowhere is worse than staying
/// quiet — and the health-data disclosure acknowledgement, which the mobile app needs over HTTP
/// where the web app reads <see cref="IUserService"/> in-process.
/// </remarks>
[Authorize]
[Route("api/v1/users")]
public class UsersController : BaseApiController
{
    private readonly IUserService _users;

    public UsersController(
        IUserContext userContext,
        ILogger<UsersController> logger,
        IUserService users)
        : base(userContext, logger)
    {
        _users = users;
    }

    /// <summary>Sets the caller's IANA time zone, e.g. <c>Europe/London</c>.</summary>
    [HttpPut("me/timezone")]
    [ProducesResponseType(typeof(ApiResponse<UserResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<object>>> UpdateTimeZone(
        [FromBody] UpdateTimeZoneRequest request, CancellationToken ct)
    {
        var userId = UserContext.UserId;
        if (!UserContext.IsAuthenticated || userId == Guid.Empty)
            return Error("We couldn't find your account — please sign in again.", StatusCodes.Status403Forbidden);

        if (string.IsNullOrWhiteSpace(request?.TimeZoneId))
            return Error("Please choose a time zone.");

        try
        {
            if (!await _users.UpdateTimeZoneAsync(userId, request.TimeZoneId.Trim(), ct))
                return Error($"'{request.TimeZoneId}' isn't a time zone we recognise.");

            return Success("Time zone updated.");
        }
        catch (KeyNotFoundException ex)
        {
            return Error(ex.Message, StatusCodes.Status404NotFound);
        }
    }

    /// <summary>
    /// Whether the caller has dismissed the Google-mandated health-data disclosure. A client shows
    /// the banner until this says they have.
    /// </summary>
    [HttpGet("me/health-data-disclosure")]
    [ProducesResponseType(typeof(ApiResponse<HealthDataDisclosureResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ApiResponse<HealthDataDisclosureResponse>>> GetHealthDataDisclosure()
    {
        if (!UserContext.IsAuthenticated || string.IsNullOrWhiteSpace(UserContext.Auth0UserId))
            return Error("We couldn't find your account — please sign in again.", StatusCodes.Status403Forbidden);

        var dismissed = await _users.HasDismissedHealthDataDisclosureAsync(UserContext.Auth0UserId);
        return Success(new HealthDataDisclosureResponse { Dismissed = dismissed });
    }

    /// <summary>
    /// Records that the caller has read the health-data disclosure. Idempotent: the first
    /// acknowledgement's timestamp is the one that is kept.
    /// </summary>
    [HttpPost("me/health-data-disclosure/dismiss")]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<object>>> DismissHealthDataDisclosure()
    {
        if (!UserContext.IsAuthenticated || string.IsNullOrWhiteSpace(UserContext.Auth0UserId))
            return Error("We couldn't find your account — please sign in again.", StatusCodes.Status403Forbidden);

        // False means no user row for this identity yet — the disclosure must keep showing rather
        // than be recorded as read against nobody.
        if (!await _users.DismissHealthDataDisclosureAsync(UserContext.Auth0UserId))
            return Error("We couldn't find your account — please sign in again.", StatusCodes.Status404NotFound);

        return Success("Thanks — we won't show that again.");
    }

    /// <summary>
    /// Whether this account is awaiting deletion, and when its data is due to go.
    /// </summary>
    /// <remarks>
    /// Read on sign-in as well as from Settings: a caregiver who asked for deletion and changed
    /// their mind finds out here that the request is still standing and can be called off.
    /// </remarks>
    [HttpGet("me/deletion")]
    [ProducesResponseType(typeof(ApiResponse<AccountDeletionStatusResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<AccountDeletionStatusResponse>>> GetAccountDeletion()
    {
        if (!UserContext.IsAuthenticated || string.IsNullOrWhiteSpace(UserContext.Auth0UserId))
            return Error("We couldn't find your account — please sign in again.", StatusCodes.Status403Forbidden);

        var status = await _users.GetDeletionStatusAsync(UserContext.Auth0UserId);
        return status is null
            ? Error("We couldn't find your account — please sign in again.", StatusCodes.Status404NotFound)
            : Success(status);
    }

    /// <summary>
    /// Asks for this account and its members' health data to be deleted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Records the request; it does not erase anything. The published policy promises erasure
    /// within 30 days, and those days are a grace period the caregiver can spend changing their
    /// mind — signing in again and calling it off is what <c>DELETE me/deletion</c> is for.
    /// </para>
    /// <para>
    /// Idempotent by design: asking twice returns the first request's due date rather than
    /// starting the 30 days over, because a restarted clock would keep the data longer than the
    /// caregiver was told it would be kept.
    /// </para>
    /// <para>
    /// A client that gets a 200 here should sign the caregiver out — an account awaiting deletion
    /// has no business going on monitoring someone.
    /// </para>
    /// </remarks>
    [HttpPost("me/deletion")]
    [ProducesResponseType(typeof(ApiResponse<AccountDeletionStatusResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<AccountDeletionStatusResponse>>> RequestAccountDeletion()
    {
        if (!UserContext.IsAuthenticated || string.IsNullOrWhiteSpace(UserContext.Auth0UserId))
            return Error("We couldn't find your account — please sign in again.", StatusCodes.Status403Forbidden);

        Logger.LogInformation(
            "Account deletion requested by Auth0 user {Auth0UserId}", UserContext.Auth0UserId);

        var status = await _users.RequestDeletionAsync(UserContext.Auth0UserId);
        return status is null
            ? Error("We couldn't find your account — please sign in again.", StatusCodes.Status404NotFound)
            : Success(status, "Your account is scheduled for deletion. Sign in before then to stop it.");
    }

    /// <summary>
    /// Calls off an outstanding deletion request.
    /// </summary>
    /// <remarks>
    /// Idempotent in the forgiving direction: cancelling when nothing was requested succeeds and
    /// reports an account that is not awaiting deletion, which is the state the caller wanted.
    /// </remarks>
    [HttpDelete("me/deletion")]
    [ProducesResponseType(typeof(ApiResponse<AccountDeletionStatusResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<AccountDeletionStatusResponse>>> CancelAccountDeletion()
    {
        if (!UserContext.IsAuthenticated || string.IsNullOrWhiteSpace(UserContext.Auth0UserId))
            return Error("We couldn't find your account — please sign in again.", StatusCodes.Status403Forbidden);

        Logger.LogInformation(
            "Account deletion cancelled by Auth0 user {Auth0UserId}", UserContext.Auth0UserId);

        var status = await _users.CancelDeletionAsync(UserContext.Auth0UserId);
        return status is null
            ? Error("We couldn't find your account — please sign in again.", StatusCodes.Status404NotFound)
            : Success(status, "Your account is staying put.");
    }
}

public class UpdateTimeZoneRequest
{
    /// <summary>IANA time zone id, e.g. <c>Europe/London</c> or <c>America/New_York</c>.</summary>
    public string? TimeZoneId { get; set; }
}
