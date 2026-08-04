using CardiTrack.Observability;
using CardiTrack.Shared;
using CardiTrack.Web.Components;
using Microsoft.AspNetCore.DataProtection;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// 1. LOGGING — same Serilog shape as CardiTrack.API: console always, plus APM
// shipping when the Apm engine is configured
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .Enrich.WithMachineName()
    .Enrich.WithEnvironmentName()
    .Enrich.WithProperty("Application", "CardiTrack.Web")
    .WriteTo.Console(
        outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}")
    .AddApmShipping(builder.Configuration.GetApmOptions())
    .CreateLogger();

builder.Host.UseSerilog();

try
{
    Log.Information("Starting CardiTrack Web");

    // 2. APM TRACING
    builder.AddApmTracing("CardiTrack.Web");

    // 3. RAZOR COMPONENTS
    builder.Services.AddRazorComponents()
        .AddInteractiveServerComponents();

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
