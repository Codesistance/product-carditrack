using CardiTrack.API.Infrastructure.UserContext;
using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Interfaces.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CardiTrack.API.Controllers;

[Route("api/v1/auth")]
public class AuthController : BaseApiController
{
    private readonly IAuth0ManagementService _auth0Management;

    public AuthController(
        IAuth0ManagementService auth0Management,
        IUserContext userContext,
        ILogger<AuthController> logger)
        : base(userContext, logger)
    {
        _auth0Management = auth0Management;
    }

    /// <summary>
    /// Resends Auth0's verification email. Anonymous by design — the caller cannot log in
    /// until verified. Always answers success (no user enumeration); rate-limited per IP.
    /// </summary>
    [HttpPost("resend-verification")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ApiResponse<bool>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<bool>>> ResendVerification(
        [FromBody] ResendVerificationRequest request, CancellationToken ct)
    {
        await _auth0Management.TrySendVerificationEmailAsync(request.Email, ct);
        return Success(true, "If that email is registered with us, a verification link is on its way!");
    }
}
