using CardiTrack.Shared;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Serilog;

namespace CardiTrack.API.Extensions;

/// <summary>
/// A second, named JWT Bearer scheme for the internal enqueue endpoint only
/// (notification_engine.md §12, §7.2 C4) — deliberately separate from
/// <see cref="Auth0Extensions.AddAuth0Authentication"/>'s default scheme, which the fallback
/// authorization policy applies to every other endpoint. <c>services.AddAuthentication()</c> can
/// be called more than once safely: each call just returns a new <c>AuthenticationBuilder</c>
/// wrapping the same <c>IServiceCollection</c>, so adding "GoogleOidc" here does not touch the
/// default scheme Auth0 already registered.
/// </summary>
public static class GoogleOidcExtensions
{
    public const string SchemeName = "GoogleOidc";

    public static IServiceCollection AddGoogleOidcAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var loader = new ConfigurationLoader(configuration);
        var audience = loader.GetRequired(ConfigurationKeys.Pipeline.Audience);
        var serviceAccount = loader.GetRequired(ConfigurationKeys.Pipeline.ServiceAccount);

        services.AddAuthentication()
            .AddJwtBearer(SchemeName, options =>
            {
                options.Authority = "https://accounts.google.com";
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = "https://accounts.google.com",
                    ValidateAudience = true,
                    ValidAudience = audience,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ClockSkew = TimeSpan.Zero
                };

                options.RequireHttpsMetadata = true;

                options.Events = new JwtBearerEvents
                {
                    OnAuthenticationFailed = context =>
                    {
                        Log.Warning("Pipeline OIDC authentication failed: {Error}", context.Exception.Message);
                        return Task.CompletedTask;
                    },

                    // Audience-pinning alone admits any GCP principal that can mint a token with
                    // this audience — pinning the caller's verified email is what closes that gap
                    // (§7.2 C4). Rejecting here, rather than in the controller, means an
                    // unauthorized caller never reaches application code at all.
                    OnTokenValidated = context =>
                    {
                        var email = context.Principal?.FindFirst("email")?.Value;
                        var verified = context.Principal?.FindFirst("email_verified")?.Value == "true";

                        if (!IsAuthorizedPipelineCaller(email, verified, serviceAccount))
                        {
                            Log.Warning(
                                "Pipeline OIDC token rejected: caller {Email} is not the pipeline service account.",
                                email);
                            context.Fail("Caller is not the pipeline service account.");
                        }

                        return Task.CompletedTask;
                    }
                };
            });

        return services;
    }

    /// <summary>
    /// The email-pinning check §7.2 C4 requires, pulled out of the <c>OnTokenValidated</c> lambda
    /// so it is unit-testable without standing up a real token-signed request.
    /// </summary>
    public static bool IsAuthorizedPipelineCaller(string? email, bool emailVerified, string expectedServiceAccount) =>
        emailVerified && string.Equals(email, expectedServiceAccount, StringComparison.OrdinalIgnoreCase);
}
