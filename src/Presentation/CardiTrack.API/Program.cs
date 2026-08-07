using AspNetCoreRateLimit;
using CardiTrack.API.Extensions;
using CardiTrack.API.Middleware;
using CardiTrack.Infrastructure.Persistence;
using CardiTrack.Observability;
using CardiTrack.Shared;
using Microsoft.EntityFrameworkCore;
using Serilog;

// Enforce UTC for all DateTime values read from PostgreSQL timestamptz columns
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", false);

// Configure Serilog
var builder = WebApplication.CreateBuilder(args);
var configLoader = new ConfigurationLoader(builder.Configuration);
builder.AddSerilogLogging();
builder.AddApmTracing("CardiTrack.API");

try
{
    Log.Information("Starting CardiTrack API");

    // 1. DATABASE
    builder.Services.AddDbContext<CardiTrackDbContext>(options =>
        options.UseNpgsql(
            configLoader.Get(ConfigurationKeys.ConnectionStrings.DefaultConnection),
            b => b.MigrationsAssembly("CardiTrack.Infrastructure")));

    // 2. AUTHENTICATION & AUTHORIZATION - Auth0 JWT
    builder.Services.AddAuth0Authentication(builder.Configuration);
    builder.Services.AddAuth0Authorization();

    // 3. CONTROLLERS & VALIDATION
    builder.Services.AddControllers();
    builder.Services.AddValidators();

    // 4. API VERSIONING
    builder.Services.AddApiVersioning(options =>
    {
        options.DefaultApiVersion = new Asp.Versioning.ApiVersion(1, 0);
        options.AssumeDefaultVersionWhenUnspecified = true;
        options.ReportApiVersions = true;
    });

    // 5. SWAGGER/OPENAPI - With JWT support
    builder.Services.AddSwaggerWithJwtSupport(builder.Configuration);

    // 6. APPLICATION SERVICES
    builder.Services.AddApplicationServices();
    builder.Services.AddInfrastructureServices(builder.Configuration);

    // 7. USER CONTEXT
    builder.Services.AddUserContextServices();

    // 8. CACHING (Redis + In-Memory)
    builder.Services.AddCachingServices(builder.Configuration);

    // 9. RATE LIMITING
    builder.Services.AddRateLimiting(builder.Configuration);

    // 10. CORS
    builder.Services.AddCors(options =>
    {
        options.AddPolicy("AllowSpecificOrigins", policy =>
        {
            policy.WithOrigins(
                    builder.Configuration.GetSection(ConfigurationKeys.Cors.AllowedOrigins).Get<string[]>()
                    ?? [])
                .AllowAnyHeader()
                .AllowAnyMethod()
                .AllowCredentials();
        });
    });

    // 11. HEALTH CHECKS
    builder.Services.AddHealthChecks();
    // .AddDbContextCheck<CardiTrackDbContext>("database")
    // .AddRedis(configLoader.Get(ConfigurationKeys.ConnectionStrings.Redis) ?? "localhost:6379", "redis");

    // 12. AUTOMAPPER
    builder.Services.AddAutoMapper(cfg => { }, AppDomain.CurrentDomain.GetAssemblies());


    var app = builder.Build();

    // MIDDLEWARE PIPELINE
    app.UseHttpsRedirection();
    // Pinned, not left to the library default. The OAuth callback carries the provider's
    // authorization code and our state token in the query string, so logging it would put a
    // live credential in Cloud Logging and the APM provider. Serilog 10 defaults this to false
    // and the Microsoft.AspNetCore level override in appsettings suppresses the hosting
    // diagnostics that would otherwise log the full URL — this makes the guarantee explicit
    // rather than a coincidence of two defaults. Do not set to true.
    app.UseSerilogRequestLogging(options => options.IncludeQueryInRequestPath = false);
    app.UseMiddleware<ExceptionHandlingMiddleware>();
    app.UseIpRateLimiting();

    if (!app.Environment.IsProduction())
    {
        app.UseSwagger();
        app.UseSwaggerUI(c =>
        {
            c.SwaggerEndpoint("/swagger/v1/swagger.json", "CardiTrack API V1");
            c.RoutePrefix = "swagger";
        });
    }

    app.UseCors("AllowSpecificOrigins");
    app.UseAuthentication();
    app.UseMiddleware<UserContextMiddleware>();
    app.UseAuthorization();
    app.MapControllers();
    app.MapHealthChecks("/health")
        .AllowAnonymous()
        .AddEndpointFilter(async (context, next) =>
        {
            var loader = context.HttpContext.RequestServices.GetRequiredService<ConfigurationLoader>();
            var expected = loader.GetRequired(ConfigurationKeys.Health.Token);
            var provided = context.HttpContext.Request.Headers["X-Health-Token"].ToString();
            if (provided != expected)
                return Results.Unauthorized();
            return await next(context);
        });

    Log.Information("CardiTrack API started successfully");
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application failed to start");
    throw;
}
finally
{
    Log.CloseAndFlush();
}
