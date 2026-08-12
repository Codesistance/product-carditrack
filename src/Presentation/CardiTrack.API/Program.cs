using AspNetCoreRateLimit;
using CardiTrack.API.Extensions;
using CardiTrack.API.Middleware;
using CardiTrack.Infrastructure.Extensions;
using CardiTrack.Infrastructure.Persistence;
using CardiTrack.Observability;
using CardiTrack.Shared;
using Serilog;
using Serilog.Events;

// Enforce UTC for all DateTime values read from PostgreSQL timestamptz columns
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", false);

// Configure Serilog
var builder = WebApplication.CreateBuilder(args);
var configLoader = new ConfigurationLoader(builder.Configuration);
builder.AddSerilogLogging();
builder.AddApmTracing(ApmServiceNames.Api);

try
{
    Log.Information("Starting CardiTrack API");

    // 1. DATABASE
    var dbConnectionString = configLoader.Get(ConfigurationKeys.ConnectionStrings.DefaultConnection);
    builder.Services.AddDbContext<CardiTrackDbContext>(options =>
        options.UseCardiTrackNpgsql(
            dbConnectionString,
            b => b.MigrationsAssembly("CardiTrack.Infrastructure")));

    // Audit writes must not share the request's context — a SaveChanges there would flush
    // whatever else the request had tracked, and the entry would die with a rolled-back
    // transaction. The factory hands the audit repository its own short-lived context.
    builder.Services.AddDbContextFactory<CardiTrackDbContext>(options =>
        options.UseCardiTrackNpgsql(
            dbConnectionString,
            b => b.MigrationsAssembly("CardiTrack.Infrastructure")),
        lifetime: ServiceLifetime.Scoped);

    // 2. AUTHENTICATION & AUTHORIZATION - Auth0 JWT (default scheme, every endpoint unless
    // [AllowAnonymous]) plus a second named scheme for the AI pipeline's internal enqueue call
    // only (notification_engine.md §7.2 C4) — never the default, so the fallback policy below
    // still applies Auth0 everywhere else.
    builder.Services.AddAuth0Authentication(builder.Configuration);
    builder.Services.AddGoogleOidcAuthentication(builder.Configuration);
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
            // Every controller carries its version in a literal route ("api/v1/..."), so the
            // query string is the only place a version can actually arrive from. The default
            // reader also probes for a {version:apiVersion} URL segment, which no route here
            // declares — work done on every request that can never match.
            options.ApiVersionReader = new Asp.Versioning.QueryStringApiVersionReader();
        })
        // Controller-based API: without this the MVC conventions that read versioning metadata
        // off controllers and actions are never registered.
        .AddMvc()
        .AddApiExplorer(options =>
        {
            // Resolves to "v1", matching the SwaggerDoc name and the /swagger/v1/swagger.json
            // endpoint served below. Not cosmetic: the versioned explorer stamps a group name
            // on every API description and Swashbuckle only includes a description whose group
            // matches the document being generated, so leaving the format at its default (empty)
            // publishes a v1 document with no paths in it.
            options.GroupNameFormat = "'v'VVV";
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
    app.UseSerilogRequestLogging(options =>
    {
        options.IncludeQueryInRequestPath = false;
        // Probe traffic is dropped rather than logged, matching the trace filter in
        // ApmExtensions. Cloud Run probes continuously, so once a service is turned up to
        // Information the probe would otherwise outnumber real requests in Cloud Logging and
        // the APM sink — the noise that makes a raised level unusable. Verbose sits below
        // every level deployed today, so nothing emits it in practice — a service
        // deliberately turned down to Verbose would log probes too. A probe that throws
        // or 500s is not a probe worth hiding and keeps the normal Error level.
        options.GetLevel = (httpContext, _, exception) =>
        {
            if (exception is not null || httpContext.Response.StatusCode >= 500)
                return LogEventLevel.Error;

            var path = httpContext.Request.Path;
            return path.StartsWithSegments("/health") || path.StartsWithSegments("/healthz")
                ? LogEventLevel.Verbose
                : LogEventLevel.Information;
        };
    });
    // Outside ExceptionHandlingMiddleware, deliberately. Everything this middleware does
    // happens on the way back out, so it still sees a populated user context and a resolved
    // endpoint — but it now also sees the status the exception handler produced. Registered
    // inside the handler it would be skipped entirely whenever an action threw, losing exactly
    // the 401/404/500 outcomes an audit trail exists to record.
    app.UseMiddleware<AuditLoggingMiddleware>();
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
    await ApmExtensions.FlushLogsAsync();
}
