using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Services;
using CardiTrack.Infrastructure.Extensions;
using CardiTrack.Infrastructure.Persistence;
using CardiTrack.Infrastructure.Repositories;
using CardiTrack.Observability;
using CardiTrack.Shared;
using CardiTrack.Web.Components;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Serilog;

// Enforce UTC for all DateTime values read from PostgreSQL timestamptz columns
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", false);

var builder = WebApplication.CreateBuilder(args);

// 1. LOGGING — same Serilog shape as CardiTrack.API: console always, plus APM
// shipping when the Apm engine is configured
Log.Logger = SerilogBootstrap.CreateLogger(builder.Configuration, "CardiTrack.Web", ApmServiceNames.Web);

builder.Host.UseSerilog();

try
{
    Log.Information("Starting CardiTrack Web");

    // 2. APM TRACING
    builder.AddApmTracing(ApmServiceNames.Web);

    // 3. RAZOR COMPONENTS
    builder.Services.AddRazorComponents()
        .AddInteractiveServerComponents();

    // 3a. AUTH STATE — cascade the (currently unauthenticated) principal so
    // components like the health-data disclosure banner can read it; they light
    // up automatically once Auth0 web login is wired
    builder.Services.AddCascadingAuthenticationState();

    // 3b. DATABASE + USER PREFERENCES — the disclosure-dismissed flag is stored
    // per user, so it follows the user across devices and sessions. Repository
    // block mirrors CardiTrack.Worker (UnitOfWork requires every repository).
    builder.Services.AddDbContext<CardiTrackDbContext>(options =>
        options.UseCardiTrackNpgsql(new ConfigurationLoader(builder.Configuration)
            .Get(ConfigurationKeys.ConnectionStrings.DefaultConnection)));
    builder.Services.AddScoped<IOrganizationRepository, OrganizationRepository>();
    builder.Services.AddScoped<IUserRepository, UserRepository>();
    builder.Services.AddScoped<ICardiMemberRepository, CardiMemberRepository>();
    builder.Services.AddScoped<ISubscriptionRepository, SubscriptionRepository>();
    builder.Services.AddScoped<IUserCardiMemberRepository, UserCardiMemberRepository>();
    builder.Services.AddScoped<IDeviceConnectionRepository, DeviceConnectionRepository>();
    builder.Services.AddScoped<IActivityLogRepository, ActivityLogRepository>();
    builder.Services.AddScoped<IDeviceActivityLogRepository, DeviceActivityLogRepository>();
    builder.Services.AddScoped<IDeviceRepository, DeviceRepository>();
    builder.Services.AddScoped<IAlertRepository, AlertRepository>();
    builder.Services.AddScoped<IPatternBaselineRepository, PatternBaselineRepository>();
    builder.Services.AddScoped<IGranularMetricRepository, GranularMetricRepository>();
    builder.Services.AddScoped<IDigestRepository, DigestRepository>();
    builder.Services.AddScoped<IRealtimeAssessmentRepository, RealtimeAssessmentRepository>();
    builder.Services.AddScoped<INotificationRepository, NotificationRepository>();
    builder.Services.AddScoped<INotificationMuteRepository, NotificationMuteRepository>();
    builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();
    builder.Services.AddScoped<IUserService, UserService>();

    // 4. HTTP CLIENT FACTORY — named client targeting the CardiTrack API
    builder.Services.AddSingleton<ConfigurationLoader>();
    builder.Services.AddHttpClient("CardiTrackApiClient", (sp, client) =>
    {
        var loader = sp.GetRequiredService<ConfigurationLoader>();
        client.BaseAddress = new Uri(loader.GetRequired(ConfigurationKeys.Api.BaseUrl));
        client.DefaultRequestHeaders.Add("Accept", "application/json");
    });

    // 5. DATA PROTECTION — antiforgery tokens must survive container recycling
    // and validate across Cloud Run instances, so the key ring persists to a
    // GCS-backed volume. Unset locally, keeping the default container-local store.
    var dataProtectionKeysPath = new ConfigurationLoader(builder.Configuration)
        .Get(ConfigurationKeys.DataProtection.KeysPath);
    if (!string.IsNullOrWhiteSpace(dataProtectionKeysPath))
    {
        builder.Services.AddDataProtection()
            .SetApplicationName("CardiTrack.Web")
            .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeysPath));
    }

    var app = builder.Build();

    // MIDDLEWARE PIPELINE
    if (!app.Environment.IsDevelopment())
    {
        app.UseExceptionHandler("/Error", createScopeForErrors: true);
        app.UseHsts();
    }
    app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
    app.UseHttpsRedirection();
    app.UseSerilogRequestLogging();

    app.UseAntiforgery();

    app.MapStaticAssets();
    app.MapRazorComponents<App>()
        .AddInteractiveServerRenderMode();

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application failed to start");
}
finally
{
    Log.CloseAndFlush();
}
