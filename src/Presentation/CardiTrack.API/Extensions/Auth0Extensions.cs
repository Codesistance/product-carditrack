using System.Security.Claims;
using CardiTrack.Shared;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Serilog;

namespace CardiTrack.API.Extensions;

public static class Auth0Extensions
{
    public static IServiceCollection AddAuth0Authentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var loader = new ConfigurationLoader(configuration);
        var domain = loader.GetRequired(ConfigurationKeys.Auth0.Domain);
        var audience = loader.GetRequired(ConfigurationKeys.Auth0.Audience);

        // ForContext gives these events a SourceContext so the per-class
        // Serilog MinimumLevel.Override in appsettings can target them.
        var logger = Log.ForContext(typeof(Auth0Extensions));

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.Authority = $"https://{domain}/";
                options.Audience = audience;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = $"https://{domain}/",
                    ValidateAudience = true,
                    ValidAudience = audience,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ClockSkew = TimeSpan.Zero,
                    NameClaimType = ClaimTypes.NameIdentifier
                };

                options.RequireHttpsMetadata = true;

                options.Events = new JwtBearerEvents
                {
                    OnAuthenticationFailed = context =>
                    {
                        logger.Warning("JWT Authentication failed: {Error}", context.Exception.Message);
                        return Task.CompletedTask;
                    },
                    OnTokenValidated = context =>
                    {
                        var userId = context.Principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                        logger.Debug("JWT validated for user: {UserId}", userId);
                        return Task.CompletedTask;
                    }
                };
            });

        return services;
    }

    public static IServiceCollection AddAuth0Authorization(this IServiceCollection services)
    {
        services.AddAuthorization(options =>
        {
            // Default policy - require authenticated user
            options.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build();

            // Business admin policy
            options.AddPolicy("RequireAdmin", policy =>
                policy.RequireClaim("role", "Admin"));

            // Business account policy
            options.AddPolicy("RequireBusinessAccount", policy =>
                policy.RequireClaim("organization_type", "Business"));

            // Family account policy
            options.AddPolicy("RequireFamilyAccount", policy =>
                policy.RequireClaim("organization_type", "Family"));
        });

        return services;
    }
}
