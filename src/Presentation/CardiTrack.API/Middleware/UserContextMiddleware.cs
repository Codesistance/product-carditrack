using System.Security.Claims;
using CardiTrack.API.Infrastructure.UserContext;
using CardiTrack.Application.Interfaces.Repositories;
using Serilog.Context;

namespace CardiTrack.API.Middleware;

public class UserContextMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<UserContextMiddleware> _logger;

    public UserContextMiddleware(RequestDelegate next, ILogger<UserContextMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(
        HttpContext httpContext,
        IUserContext userContext,
        IUserRepository userRepository)
    {
        if (httpContext.User.Identity?.IsAuthenticated == true)
        {
            try
            {
                var auth0UserId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                var email = httpContext.User.FindFirst(ClaimTypes.Email)?.Value ?? string.Empty;
                var emailVerified = ParseEmailVerified(httpContext.User);

                if (!string.IsNullOrEmpty(auth0UserId))
                {
                    var locale = ParseLocale(httpContext.Request.Headers.AcceptLanguage.ToString());

                    // Set basic claims from JWT
                    if (userContext is UserContext concreteContext)
                    {
                        concreteContext.SetAuthenticatedUser(auth0UserId, email, locale, emailVerified);

                        // Enrich with the database identity; absent during onboarding
                        // (before POST /api/Onboarding/user), so UserId stays Guid.Empty.
                        var user = await userRepository.GetByAuth0UserIdAsync(auth0UserId);
                        if (user is not null)
                        {
                            concreteContext.SetFullUserContext(user.Id, user.OrganizationId, user.Role);
                        }
                    }

                    // Correlation only. Auth0UserId is pseudonymous and stays; the email address
                    // does not — pushed here it rode on every log line in the request, into Cloud
                    // Logging and the APM provider, making a health service's user list readable
                    // from telemetry. Resolve an Auth0UserId against the database when a support
                    // question actually needs the person.
                    LogContext.PushProperty("Auth0UserId", auth0UserId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to set user context from JWT claims");
            }
        }

        await _next(httpContext);
    }

    // email_verified reaches the access token only via the tenant's post-login Action
    // (namespaced claim; bare name accepted for tests/other issuers). Absent claim => null.
    private static bool? ParseEmailVerified(ClaimsPrincipal user)
    {
        var raw = user.FindFirst("https://carditrack.com/email_verified")?.Value
            ?? user.FindFirst("email_verified")?.Value;
        return bool.TryParse(raw, out var verified) ? verified : null;
    }

    // Parses the first language tag from Accept-Language (e.g. "en-GB,en;q=0.9" → "en-GB")
    private static string ParseLocale(string acceptLanguage)
    {
        if (string.IsNullOrWhiteSpace(acceptLanguage))
            return "en-US";

        var first = acceptLanguage.Split(',')[0].Split(';')[0].Trim();
        return string.IsNullOrEmpty(first) ? "en-US" : first;
    }
}
