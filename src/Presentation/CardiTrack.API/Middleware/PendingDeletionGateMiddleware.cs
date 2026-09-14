using System.Net.Mime;
using System.Text.Json;
using CardiTrack.API.Infrastructure.UserContext;
using CardiTrack.Application.DTOs.Responses;

namespace CardiTrack.API.Middleware;

/// <summary>
/// Refuses everything but reading and cancelling a deletion, once an account has asked to be
/// deleted.
/// </summary>
/// <remarks>
/// <para>
/// Requesting deletion is meant to stop the account working, and until this existed it did not:
/// the client signed itself out, but a second device or a still-valid bearer token carried on
/// reading health data as though nothing had happened. Signing a client out is not an
/// authorization boundary; this is.
/// </para>
/// <para>
/// <strong>This deliberately stops the monitoring too</strong>, and that is a decision with a cost
/// worth stating where someone will read it. A member whose only caregiver has asked to be deleted
/// is not watched for the 30 days the request can still be cancelled: no sync, no alerts. The
/// alternative — going on collecting a vulnerable person's health data for a month after the one
/// person responsible for them asked for it all to be deleted — was judged worse. The terms of
/// service say so in as many words, because a family must not discover it from silence.
/// </para>
/// <para>
/// The account is not dormant to its owner: signing in still works, and
/// <c>DELETE /api/v1/users/me/deletion</c> calls the whole thing off and restores everything,
/// which is the only reason a 30-day window exists at all. Those endpoints are the exceptions
/// below.
/// </para>
/// </remarks>
public class PendingDeletionGateMiddleware
{
    /// <summary>
    /// The one path an account awaiting deletion may still use — read its status, or cancel.
    /// </summary>
    /// <remarks>
    /// POST is allowed as well as GET and DELETE: re-requesting is idempotent and simply reports
    /// the standing due date, and refusing it would make a retrying client look broken for asking
    /// a question whose answer has not changed.
    /// </remarks>
    private const string DeletionPath = "/api/v1/users/me/deletion";

    private readonly RequestDelegate _next;
    private readonly ILogger<PendingDeletionGateMiddleware> _logger;

    public PendingDeletionGateMiddleware(
        RequestDelegate next, ILogger<PendingDeletionGateMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, IUserContext userContext)
    {
        if (userContext.DeletionRequestedAtUtc is null || IsDeletionEndpoint(context.Request.Path))
        {
            await _next(context);
            return;
        }

        // Logged at information, not warning: this is the gate working, not a fault. It is worth a
        // line because "the app stopped working" is exactly what a caregiver would report, and the
        // answer is in this one entry.
        _logger.LogInformation(
            "Refused {Method} {Path} for a user awaiting deletion since {RequestedAt:o}.",
            context.Request.Method, context.Request.Path, userContext.DeletionRequestedAtUtc);

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = MediaTypeNames.Application.Json;

        await context.Response.WriteAsync(JsonSerializer.Serialize(new ErrorResponse
        {
            Success = false,
            Message = "Your account is scheduled for deletion, so monitoring has stopped. "
                      + "Sign in and cancel the deletion to carry on.",
        }));
    }

    private static bool IsDeletionEndpoint(PathString path) =>
        path.Equals(DeletionPath, StringComparison.OrdinalIgnoreCase);
}
