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
using Microsoft.AspNetCore.HttpOverrides;
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
    builder.Services.AddScoped<IMemberQuestionnaireRepository, MemberQuestionnaireRepository>();
    builder.Services.AddScoped<IEnvironmentalReadingRepository, EnvironmentalReadingRepository>();
    builder.Services.AddScoped<INotificationRepository, NotificationRepository>();
    builder.Services.AddScoped<INotificationMuteRepository, NotificationMuteRepository>();
    builder.Services.AddScoped<IAlertPreferenceRepository, AlertPreferenceRepository>();
    // UnitOfWork's constructor takes every repository, so each host must register all of them
    // even when it never touches the feature — see the same block in the Worker's Program.cs.
    builder.Services.AddScoped<IMemberChatSessionRepository, MemberChatSessionRepository>();
    builder.Services.AddScoped<IMemberChatTurnRepository, MemberChatTurnRepository>();
    builder.Services.AddScoped<IMemberChatTurnUsageRepository, MemberChatTurnUsageRepository>();
    builder.Services.AddScoped<IMemberStatusLineRepository, MemberStatusLineRepository>();
    builder.Services.AddScoped<IReportRepository, ReportRepository>();
    builder.Services.AddScoped<IMemberAdviseRepository, MemberAdviseRepository>();
    builder.Services.AddPushRepositories();
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

    // 6. REVERSE PROXY + HTTPS — Cloud Run terminates TLS at its front end and forwards
    // plain HTTP to the container, so without reading X-Forwarded-Proto the app believes
    // every request arrived over http: UseHsts() below never emits a header, and
    // UseHttpsRedirection() has no https binding to derive a port from, so it logs
    // "Failed to determine the https port for redirect" and passes the request through
    // unredirected. Honouring the header restores the real scheme; HttpsPort then only
    // applies to the requests that genuinely arrived in cleartext (Cloud Run serves the
    // http endpoint too), which are the ones that should be redirected. Reading the scheme
    // first is also what stops the pinned port turning every proxied request into a
    // redirect loop.
    //
    // KnownIPNetworks/KnownProxies are cleared because the front end is neither on loopback
    // nor at an address knowable ahead of time. The container is only reachable through it,
    // it appends its own value last, and the default ForwardLimit of 1 reads only that last
    // value — so a client-supplied header cannot win. Local development keeps the framework
    // defaults, where the launch profile's https binding already supplies the port.
    if (!builder.Environment.IsDevelopment())
    {
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedProto;
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
        });
        builder.Services.AddHttpsRedirection(options => options.HttpsPort = 443);
    }

    var app = builder.Build();

    // MIDDLEWARE PIPELINE
    if (!app.Environment.IsDevelopment())
    {
        // Ahead of HSTS and the redirect below — both branch on the request scheme, so they
        // have to run after it has been corrected from the forwarded header.
        app.UseForwardedHeaders();
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
    await ApmExtensions.FlushLogsAsync();
}
